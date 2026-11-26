namespace DeadlockVoiceLab.Audio;

public sealed record SegmenterOptions(
    string Mode = "vad",
    float ThresholdDb = -48f,
    float MinCenterCoherence = 0.25f,
    int MinSpeechMs = 220,
    int SilenceHangoverMs = 450,
    int PreRollMs = 250,
    int MaxSegmentSeconds = 7,
    bool UseAdaptiveThreshold = true,
    int SoftSplitMs = 3800,
    int SoftSplitSilenceMs = 120,
    float? QuietThresholdDb = null,
    int ManualPauseMs = 0)
{
    // Only the game HUD can open this gate. The quiet floor still tracks
    // the background level and requires a longer stretch of centered audio.
    public static SegmenterOptions TeammateVoice { get; } =
        new(Mode: "manual", PreRollMs: 200, MaxSegmentSeconds: 3, QuietThresholdDb: -58f, ManualPauseMs: 350);
}

public sealed record CompletedAudioSegment(
    float[] RawStereo48k,
    float[] RawMono16k,
    float[] CleanMono16k,
    byte[] RawWav,
    byte[] CleanWav,
    int DurationMs,
    float RawDb,
    float CleanDb,
    float CenterCoherence,
    double DspMs,
    bool IsStereoSource,
    string TriggerMode,
    long Revision);

public readonly record struct MeterSample(
    float RawDb,
    float CleanDb,
    float SpeechDb,
    float CenterCoherence,
    bool VadTriggered,
    bool ManualGateOpen,
    bool IsCapturingSegment,
    float EffectiveThresholdDb);

/// <summary>
/// Cuts continuous audio into speech segments via either center-coherent VAD
/// or the teammate speaking state reported by Panorama.
/// </summary>
public sealed class VoiceSegmenter
{
    private readonly object gate = new();
    private SegmenterOptions options;
    private bool isStereoSource;

    // Aligned rolling pre-roll buffers
    private readonly List<float> preStereo48k = new();
    private readonly List<float> preRaw16k = new();
    private readonly List<float> preClean16k = new();

    // Active utterance accumulators
    private readonly List<float> segStereo48k = new();
    private readonly List<float> segRaw16k = new();
    private readonly List<float> segClean16k = new();
    private long segmentRevision;
    private bool recording;
    private bool manualOpen;
    private bool waitingForManualSpeech;
    private float bgFloorDb = -52f;
    private int bgInitChunks;
    private double recordedMs;
    private double activeSpeechMs;
    private double quietSpeechMs;
    private double lastQuietCheckMs = -1;
    private bool quietRecovered;
    private double manualPauseMs;
    private int preRollSamples;
    private double silenceMs;
    private double softSilenceMs;
    private float segPeakDb = -80f;
    private double dspMsAcc;
    private double rawDbWeightSum;
    private double cleanDbWeightSum;
    private double cohWeightSum;
    private int weightFrames;

    // 300Hz-3400Hz bandpass filter at 16kHz for speech-band energy estimation
    private BandPass16k speechFilter = new();

    public VoiceSegmenter(SegmenterOptions? initial = null, bool isStereoSource = true)
    {
        options = Sanitize(initial ?? new SegmenterOptions());
        this.isStereoSource = isStereoSource;
        speechFilter.Configure(1010f, OutputBandwidthHz: 3100f, AudioDspPipeline.OutputSampleRate);
    }

    public SegmenterOptions Options
    {
        get { lock (gate) return options; }
    }

    public void UpdateOptions(SegmenterOptions next)
    {
        var clean = Sanitize(next);
        lock (gate)
        {
            options = clean;
            if (clean.Mode != "manual") manualOpen = false;
            if (clean.ManualPauseMs == 0 || clean.Mode != "manual") waitingForManualSpeech = false;
        }
    }

    public void Reset(bool? stereoSource = null)
    {
        lock (gate)
        {
            if (stereoSource.HasValue) isStereoSource = stereoSource.Value;
            preStereo48k.Clear();
            preRaw16k.Clear();
            preClean16k.Clear();
            ResetSegment();
            manualOpen = false;
            waitingForManualSpeech = false;
            bgFloorDb = -52f;
            bgInitChunks = 0;
            speechFilter.Reset();
        }
    }

    public static SegmenterOptions Sanitize(SegmenterOptions o) => new(
        Mode: o.Mode == "manual" ? "manual" : "vad",
        ThresholdDb: Math.Clamp(float.IsFinite(o.ThresholdDb) ? o.ThresholdDb : -48f, -70f, -10f),
        MinCenterCoherence: Math.Clamp(float.IsFinite(o.MinCenterCoherence) ? o.MinCenterCoherence : 0.25f, 0f, 0.9f),
        MinSpeechMs: Math.Clamp(o.MinSpeechMs, 100, 3000),
        SilenceHangoverMs: Math.Clamp(o.SilenceHangoverMs, 150, 3000),
        PreRollMs: Math.Clamp(o.PreRollMs, 0, 1000),
        MaxSegmentSeconds: Math.Clamp(o.MaxSegmentSeconds, 3, 30),
        UseAdaptiveThreshold: o.UseAdaptiveThreshold,
        SoftSplitMs: Math.Clamp(o.SoftSplitMs, 2000, 15000),
        SoftSplitSilenceMs: Math.Clamp(o.SoftSplitSilenceMs, 80, 500),
        QuietThresholdDb: o.QuietThresholdDb is float floor && float.IsFinite(floor) ? Math.Clamp(floor, -70f, -10f) : null,
        ManualPauseMs: o.ManualPauseMs == 0 ? 0 : Math.Clamp(o.ManualPauseMs, 250, 1000));

    public CompletedAudioSegment[] Feed(
        ReadOnlySpan<float> stereo48k,
        AudioDspPipeline dsp,
        out MeterSample meter,
        bool allowEarlyManualSplit = true,
        Action<float[]>? captureCleanAudio = null)
    {
        var completed = new List<CompletedAudioSegment>();
        const int chunkFrames48k = 960; // 20ms chunks ensure consistent VAD & pre-roll behavior
        int totalFrames48k = stereo48k.Length / 2;
        MeterSample lastMeter = default;

        if (totalFrames48k == 0)
        {
            float eff = options.UseAdaptiveThreshold ? MathF.Max(options.ThresholdDb, bgFloorDb + 6f) : options.ThresholdDb;
            meter = new MeterSample(-80f, -80f, -80f, 1f, false, manualOpen, recording, eff);
            return [];
        }

        int framePos = 0;
        while (framePos < totalFrames48k)
        {
            int takeFrames = Math.Min(chunkFrames48k, totalFrames48k - framePos);
            var subStereo = stereo48k.Slice(framePos * 2, takeFrames * 2);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var dspRes = dsp.ProcessStereoBlock(subStereo);
            sw.Stop();
            captureCleanAudio?.Invoke(dspRes.Clean16k);

            var seg = AcceptProcessedChunk(
                subStereo,
                dspRes.Raw16k,
                dspRes.Clean16k,
                dspRes.RawDb,
                dspRes.CleanDb,
                dspRes.CenterCoherence,
                sw.Elapsed.TotalMilliseconds,
                dsp.Options.EnableNormalize,
                allowEarlyManualSplit,
                out lastMeter);

            if (seg != null) completed.Add(seg);
            framePos += takeFrames;
        }

        meter = lastMeter;
        return completed.ToArray();
    }

    public CompletedAudioSegment? SetManualGate(bool open, bool enableNormalize = true)
    {
        lock (gate)
        {
            if (open != manualOpen) waitingForManualSpeech = false;
            manualOpen = open;
            if (options.Mode != "manual") return null;

            if (open)
            {
                if (!recording && !waitingForManualSpeech)
                {
                    StartSegmentFromPreRoll(-40f);
                }
                return null;
            }
            else if (recording)
            {
                return FinalizeSegment(enableNormalize, minKeepMs: 120, triggerMode: "manual", keepOverlap: false);
            }
            return null;
        }
    }

    public CompletedAudioSegment? FlushPending(bool enableNormalize = true)
    {
        lock (gate)
        {
            if (!recording) return null;
            int minKeep = options.Mode == "manual" ? 120 : options.MinSpeechMs;
            return FinalizeSegment(enableNormalize, minKeep, options.Mode, keepOverlap: false);
        }
    }

    internal long SegmentRevision { get { lock (gate) return segmentRevision; } }

    internal float[]? SnapshotPreview(int minimumSamples, int minimumSpeechMs = 120,
        double minimumTimeBeforeSplitMs = 0)
    {
        float[] samples;
        lock (gate)
        {
            if (!recording || segClean16k.Count < minimumSamples ||
                minimumTimeBeforeSplitMs > 0 && recordedMs + minimumTimeBeforeSplitMs >= options.MaxSegmentSeconds * 1000 ||
                activeSpeechMs < minimumSpeechMs && quietSpeechMs < Math.Max(160, minimumSpeechMs) || manualPauseMs >= 160)
                return null;
            samples = segClean16k.ToArray();
        }
        AudioDspPipeline.NormalizeSpeech(samples);
        return samples;
    }

    private CompletedAudioSegment? AcceptProcessedChunk(
        ReadOnlySpan<float> stereo48k,
        float[] raw16k,
        float[] clean16k,
        float rawDb,
        float cleanDb,
        float coherence,
        double chunkDspMs,
        bool enableNormalize,
        bool allowEarlyManualSplit,
        out MeterSample meter)
    {
        lock (gate)
        {
            float speechDb = ComputeSpeechBandDb(clean16k);
            double chunkMs = clean16k.Length > 0
                ? clean16k.Length * 1000.0 / AudioDspPipeline.OutputSampleRate
                : (stereo48k.Length / 2) * 1000.0 / AudioDspPipeline.WorkingSampleRate;

            bool coherenceOk = !isStereoSource || coherence >= options.MinCenterCoherence;

            if (speechDb <= -76f)
            {
                bgFloorDb = 0.80f * bgFloorDb + 0.20f * (-68f);
                bgInitChunks = Math.Min(bgInitChunks + 1, 10);
            }
            else if (bgInitChunks == 0)
            {
                bgFloorDb = Math.Clamp(speechDb, -68f, -36f);
                bgInitChunks = 1;
            }
            else if (bgInitChunks < 10)
            {
                bgFloorDb = speechDb < bgFloorDb
                    ? (0.75f * bgFloorDb + 0.25f * speechDb)
                    : (0.95f * bgFloorDb + 0.05f * speechDb);
                bgInitChunks++;
            }
            else if (speechDb < bgFloorDb + 3.0f)
            {
                bgFloorDb = 0.96f * bgFloorDb + 0.04f * speechDb;
            }
            else if (speechDb < bgFloorDb + 12.0f)
            {
                bgFloorDb += 0.09f; // ~4.5 dB/sec adaptation to sustained combat background
            }
            else
            {
                bgFloorDb += 0.02f; // slow drift during loud foreground speech
            }
            bgFloorDb = Math.Clamp(bgFloorDb, -68f, -22f);

            float startThreshold = options.UseAdaptiveThreshold
                ? MathF.Max(options.ThresholdDb, bgFloorDb + 6.0f)
                : options.ThresholdDb;
            float keepThreshold = MathF.Max(options.ThresholdDb - 1.5f, startThreshold - 1.5f);

            bool startTriggered = speechDb >= startThreshold && coherenceOk;
            bool keepTriggered = speechDb >= keepThreshold && coherenceOk;
            bool vadTriggered = recording ? keepTriggered : startTriggered;

            CompletedAudioSegment? finalized = null;

            if (options.Mode == "manual")
            {
                float quietFloor = options.QuietThresholdDb ?? options.ThresholdDb;
                float quietThreshold = options.UseAdaptiveThreshold ? MathF.Max(quietFloor, bgFloorDb + 6f) : quietFloor;
                bool aboveQuietThreshold = speechDb >= quietThreshold - 1.5f;
                bool perceivedSpeech = aboveQuietThreshold && coherenceOk;
                if (manualOpen && !recording && (!waitingForManualSpeech || perceivedSpeech))
                {
                    waitingForManualSpeech = false;
                    StartSegmentFromPreRoll(speechDb);
                }
                if (recording)
                {
                    if (manualOpen && perceivedSpeech && options.QuietThresholdDb.HasValue)
                        quietSpeechMs += chunkMs;
                    AppendToSegment(stereo48k, raw16k, clean16k, chunkMs, rawDb, cleanDb, speechDb, coherence, chunkDspMs, startThreshold, isSpeech: keepTriggered, coherenceOk: coherenceOk);
                    // Confirm a genuinely quiet gap. A raised background floor
                    // can reject weak syllables but must not make them a cut point.
                    // Quiet-only sentences need a deeper gap to distinguish a
                    // pause from their own fading syllables.
                    float pauseFloor = quietFloor - (activeSpeechMs >= 120 ? 6f : 14f);
                    manualPauseMs = speechDb >= pauseFloor ? 0 : manualPauseMs + chunkMs;
                    bool endedAtPause = options.ManualPauseMs > 0 && manualPauseMs >= options.ManualPauseMs
                        && allowEarlyManualSplit && (activeSpeechMs >= 120 || CanRecoverQuietSpeech("manual"));
                    if (!manualOpen || recordedMs >= options.MaxSegmentSeconds * 1000.0 || endedAtPause)
                    {
                        finalized = FinalizeSegment(enableNormalize, minKeepMs: 120, triggerMode: "manual", keepOverlap: false);
                        waitingForManualSpeech = endedAtPause && manualOpen;
                    }
                }
                else
                {
                    PushPreRoll(stereo48k, raw16k, clean16k);
                }
            }
            else
            {
                if (!recording)
                {
                    if (startTriggered)
                    {
                        StartSegmentFromPreRoll(speechDb);
                        AppendToSegment(stereo48k, raw16k, clean16k, chunkMs, rawDb, cleanDb, speechDb, coherence, chunkDspMs, startThreshold, isSpeech: true, coherenceOk: coherenceOk);
                    }
                    else
                    {
                        PushPreRoll(stereo48k, raw16k, clean16k);
                    }
                }
                else
                {
                    AppendToSegment(stereo48k, raw16k, clean16k, chunkMs, rawDb, cleanDb, speechDb, coherence, chunkDspMs, startThreshold, isSpeech: keepTriggered, coherenceOk: coherenceOk);
                    bool shouldCut = silenceMs >= options.SilenceHangoverMs
                                     || (options.UseAdaptiveThreshold && recordedMs >= options.SoftSplitMs && softSilenceMs >= options.SoftSplitSilenceMs)
                                     || recordedMs >= options.MaxSegmentSeconds * 1000.0;
                    if (shouldCut)
                    {
                        bool keepOverlap = silenceMs < options.SilenceHangoverMs;
                        finalized = FinalizeSegment(enableNormalize, minKeepMs: options.MinSpeechMs, triggerMode: "vad", keepOverlap: keepOverlap);
                    }
                }
            }

            meter = new MeterSample(rawDb, cleanDb, speechDb, coherence, vadTriggered, manualOpen, recording, startThreshold);
            return finalized;
        }
    }

    private float ComputeSpeechBandDb(ReadOnlySpan<float> clean16k)
    {
        if (clean16k.Length == 0) return -80f;
        double sum = 0;
        for (int i = 0; i < clean16k.Length; i++)
        {
            float y = speechFilter.Process(clean16k[i]);
            sum += y * (double)y;
        }
        return AudioDspPipeline.AmplitudeToDb((float)Math.Sqrt(sum / clean16k.Length));
    }

    private void PushPreRoll(ReadOnlySpan<float> stereo48k, float[] raw16k, float[] clean16k)
    {
        if (options.PreRollMs <= 0)
        {
            preStereo48k.Clear();
            preRaw16k.Clear();
            preClean16k.Clear();
            return;
        }

        for (int i = 0; i < stereo48k.Length; i++) preStereo48k.Add(stereo48k[i]);
        preRaw16k.AddRange(raw16k);
        preClean16k.AddRange(clean16k);

        int max16k = (int)(options.PreRollMs * (AudioDspPipeline.OutputSampleRate / 1000.0));
        int max48kStereo = (int)(options.PreRollMs * (AudioDspPipeline.WorkingSampleRate / 1000.0)) * 2;

        if (preRaw16k.Count > max16k) preRaw16k.RemoveRange(0, preRaw16k.Count - max16k);
        if (preClean16k.Count > max16k) preClean16k.RemoveRange(0, preClean16k.Count - max16k);
        if (preStereo48k.Count > max48kStereo) preStereo48k.RemoveRange(0, preStereo48k.Count - max48kStereo);
    }

    private void StartSegmentFromPreRoll(float initialSpeechDb)
    {
        ResetSegment();
        recording = true;
        segPeakDb = initialSpeechDb;
        segStereo48k.AddRange(preStereo48k);
        segRaw16k.AddRange(preRaw16k);
        segClean16k.AddRange(preClean16k);
        preRollSamples = preClean16k.Count;
        recordedMs = segClean16k.Count * 1000.0 / AudioDspPipeline.OutputSampleRate;
        preStereo48k.Clear();
        preRaw16k.Clear();
        preClean16k.Clear();
    }

    private void AppendToSegment(
        ReadOnlySpan<float> stereo48k,
        float[] raw16k,
        float[] clean16k,
        double chunkMs,
        float rawDb,
        float cleanDb,
        float speechDb,
        float coherence,
        double chunkDspMs,
        float startThreshold,
        bool isSpeech,
        bool coherenceOk)
    {
        for (int i = 0; i < stereo48k.Length; i++) segStereo48k.Add(stereo48k[i]);
        segRaw16k.AddRange(raw16k);
        segClean16k.AddRange(clean16k);
        recordedMs += chunkMs;
        dspMsAcc += chunkDspMs;
        rawDbWeightSum += rawDb;
        cleanDbWeightSum += cleanDb;
        cohWeightSum += coherence;
        weightFrames++;

        if (speechDb > segPeakDb)
            segPeakDb = 0.70f * segPeakDb + 0.30f * speechDb;
        else
            segPeakDb = 0.985f * segPeakDb + 0.015f * speechDb;

        if (isSpeech)
        {
            activeSpeechMs += chunkMs;
            silenceMs = 0;
        }
        else
        {
            silenceMs += chunkMs;
        }

        float softDipThr = MathF.Max(startThreshold, segPeakDb - 6.0f);
        if (speechDb >= softDipThr && coherenceOk)
            softSilenceMs = 0;
        else
            softSilenceMs += chunkMs;
    }

    private CompletedAudioSegment? FinalizeSegment(bool enableNormalize, int minKeepMs, string triggerMode, bool keepOverlap)
    {
        var active = activeSpeechMs;
        if (active < minKeepMs && !CanRecoverQuietSpeech(triggerMode))
        {
            ResetSegment();
            return null;
        }

        var stereoArr = segStereo48k.ToArray();
        var rawArr = segRaw16k.ToArray();
        var cleanArr = segClean16k.ToArray();
        double totalDsp = dspMsAcc;
        int wFrames = Math.Max(1, weightFrames);
        float avgCoh = (float)(cohWeightSum / wFrames);
        long revision = segmentRevision;

        ResetSegment();

        int overlapMs = Math.Min(120, options.PreRollMs);
        if (keepOverlap && overlapMs > 0)
        {
            int take16k = Math.Min(cleanArr.Length, (int)(overlapMs * (AudioDspPipeline.OutputSampleRate / 1000.0)));
            int take48kFloats = Math.Min(stereoArr.Length, (int)(overlapMs * (AudioDspPipeline.WorkingSampleRate / 1000.0)) * 2);
            preStereo48k.Clear();
            preRaw16k.Clear();
            preClean16k.Clear();
            if (take16k > 0)
            {
                preRaw16k.AddRange(new ArraySegment<float>(rawArr, rawArr.Length - take16k, take16k));
                preClean16k.AddRange(new ArraySegment<float>(cleanArr, cleanArr.Length - take16k, take16k));
            }
            if (take48kFloats > 0)
            {
                preStereo48k.AddRange(new ArraySegment<float>(stereoArr, stereoArr.Length - take48kFloats, take48kFloats));
            }
        }

        if (cleanArr.Length == 0) return null;

        var swNorm = System.Diagnostics.Stopwatch.StartNew();
        if (enableNormalize)
        {
            AudioDspPipeline.NormalizeSpeech(cleanArr);
        }
        float rawDb = AudioDspPipeline.ComputeRmsDb(rawArr);
        float cleanDb = AudioDspPipeline.ComputeRmsDb(cleanArr);
        byte[] rawWav = WavCodec.EncodePcm16Mono(rawArr, AudioDspPipeline.OutputSampleRate);
        byte[] cleanWav = WavCodec.EncodePcm16Mono(cleanArr, AudioDspPipeline.OutputSampleRate);
        swNorm.Stop();

        int durationMs = (int)Math.Round(cleanArr.Length * 1000.0 / AudioDspPipeline.OutputSampleRate);
        return new CompletedAudioSegment(
            stereoArr,
            rawArr,
            cleanArr,
            rawWav,
            cleanWav,
            durationMs,
            rawDb,
            cleanDb,
            avgCoh,
            totalDsp + swNorm.Elapsed.TotalMilliseconds,
            isStereoSource,
            triggerMode,
            revision);
    }

    private bool CanRecoverQuietSpeech(string triggerMode)
    {
        if (triggerMode != "manual" || options.QuietThresholdDb is not float quietFloor || quietSpeechMs < 160)
            return false;
        if (quietRecovered || lastQuietCheckMs == quietSpeechMs)
            return quietRecovered;
        lastQuietCheckMs = quietSpeechMs;

        // Look for pitch periodicity only on the otherwise-rejected path.
        // The pre-roll is audible context, but cannot establish teammate speech.
        var samples = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(segClean16k)[preRollSamples..];
        const int window = 480, hop = 320; // 30 ms with a 20 ms stride at 16 kHz
        double minimumMeanSquare = Math.Pow(10, (quietFloor - 6) / 10.0);
        int voiced = 0;
        for (int start = 0; start + window <= samples.Length; start += hop)
        {
            var frame = samples.Slice(start, window);
            if (HasPitchPeriod(frame, minimumMeanSquare) && ++voiced == 2)
                return quietRecovered = true;
        }
        return false;
    }

    internal static bool HasPitchPeriod(ReadOnlySpan<float> frame, double minimumMeanSquare)
    {
        // Compare the last 280 samples with the same length at plausible voice
        // pitch lags (80–400 Hz). Normalization makes this independent of gain.
        const int begin = 200;
        double energy = 0;
        for (int i = begin; i < frame.Length; i += 2) energy += frame[i] * frame[i];
        if (energy < minimumMeanSquare * ((frame.Length - begin) / 2)) return false;
        for (int lag = 40; lag <= begin; lag += 2)
        {
            double dot = 0, shiftedEnergy = 0;
            for (int i = begin; i < frame.Length; i += 2)
            {
                double shifted = frame[i - lag];
                shiftedEnergy += shifted * shifted;
                dot += frame[i] * shifted;
            }
            const double minimumCorrelation = 0.52;
            if (dot > 0 && dot * dot >= minimumCorrelation * minimumCorrelation * energy * shiftedEnergy)
                return true;
        }
        return false;
    }

    private void ResetSegment()
    {
        segmentRevision++;
        segStereo48k.Clear();
        segRaw16k.Clear();
        segClean16k.Clear();
        recording = false;
        recordedMs = 0;
        activeSpeechMs = 0;
        quietSpeechMs = 0;
        lastQuietCheckMs = -1;
        quietRecovered = false;
        manualPauseMs = 0;
        preRollSamples = 0;
        silenceMs = 0;
        softSilenceMs = 0;
        segPeakDb = -80f;
        dspMsAcc = 0;
        rawDbWeightSum = 0;
        cleanDbWeightSum = 0;
        cohWeightSum = 0;
        weightFrames = 0;
    }

    private struct BandPass16k
    {
        private float b0, b1, b2, a1, a2, z1, z2;

        public void Configure(float centerHz, float OutputBandwidthHz, int sampleRate)
        {
            float w0 = 2f * MathF.PI * centerHz / sampleRate;
            float q = centerHz / OutputBandwidthHz;
            float cosW0 = MathF.Cos(w0);
            float sinW0 = MathF.Sin(w0);
            float alpha = sinW0 / (2f * q);
            float invA0 = 1f / (1f + alpha);

            b0 = alpha * invA0;
            b1 = 0f;
            b2 = -alpha * invA0;
            a1 = -2f * cosW0 * invA0;
            a2 = (1f - alpha) * invA0;
        }

        public void Reset() => z1 = z2 = 0f;

        public float Process(float x)
        {
            float y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return y;
        }
    }
}
