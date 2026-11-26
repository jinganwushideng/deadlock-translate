namespace DeadlockVoiceLab.Audio;

public sealed record DspOptions(
    float CenterStrength = 1.2f,
    float HighPassHz = 80f,
    float LowPassHz = 7600f,
    bool EnableNoiseGate = false,
    float NoiseGateStrength = 0.5f,
    bool EnableNormalize = true);

public readonly record struct DspBlockResult(
    float[] Raw16k,
    float[] Clean16k,
    float RawDb,
    float CleanDb,
    float CenterCoherence);

public readonly record struct OfflineDspResult(
    float[] Raw16k,
    float[] Clean16k,
    float RawDb,
    float CleanDb,
    float CenterCoherence);

/// <summary>
/// Pure C# real-time and offline audio cleaner:
/// 48kHz stereo -> STFT Mid-Side center extraction (+ optional spectral gate)
/// -> Butterworth HPF/LPF -> anti-aliased 3:1 decimator (16kHz) -> RMS normalization.
/// </summary>
public sealed class AudioDspPipeline
{
    public const int WorkingSampleRate = 48000;
    public const int OutputSampleRate = 16000;
    private const int FftSize = 512;
    private const int HopSize = 256;
    private const int HalfBins = FftSize / 2;

    private static readonly float[] SqrtHannWindow = BuildSqrtHannWindow(FftSize);
    private static readonly float[] DecimatorFir = BuildLowPassFir(19, 7000f, WorkingSampleRate);

    private readonly object gate = new();
    private DspOptions options;
    private bool isStereoSource;

    // STFT analysis/synthesis ring buffers at 48kHz
    private readonly float[] midAccum = new float[FftSize];
    private readonly float[] sideAccum = new float[FftSize];
    private readonly float[] olaBuffer = new float[FftSize];
    private int stftFill = HopSize; // pre-seed one hop of silence so hop cadence aligns cleanly

    // Per-bin smoothed spectra (0..HalfBins)
    private readonly float[] smoothMid = new float[HalfBins + 1];
    private readonly float[] smoothSide = new float[HalfBins + 1];
    private readonly float[] noiseFloor = new float[HalfBins + 1];
    private readonly float[] fftRe = new float[FftSize];
    private readonly float[] fftIm = new float[FftSize];
    private readonly float[] cleanRe = new float[FftSize];
    private readonly float[] cleanIm = new float[FftSize];

    // Raw delayed path (aligned to STFT HopSize=256 so raw16k & clean16k stay sample-aligned)
    private readonly float[] rawDelay = new float[HopSize];
    private int rawDelayIndex;

    // 4th-order Butterworth HPF (2 cascaded biquads) + 2nd-order LPF
    private BiquadFilter hp1 = new();
    private BiquadFilter hp2 = new();
    private BiquadFilter lp1 = new();

    // 3:1 FIR decimator histories
    private readonly float[] rawFirBuf = new float[19];
    private readonly float[] cleanFirBuf = new float[19];
    private int firPos;
    private int decimatePhase;

    public AudioDspPipeline(DspOptions? initial = null, bool isStereoSource = true)
    {
        this.options = Sanitize(initial ?? new DspOptions());
        this.isStereoSource = isStereoSource;
        ConfigureFilters(this.options);
        Reset();
    }

    public DspOptions Options
    {
        get { lock (gate) return options; }
    }

    public void UpdateOptions(DspOptions next)
    {
        var clean = Sanitize(next);
        lock (gate)
        {
            bool filtersChanged = Math.Abs(clean.HighPassHz - options.HighPassHz) > 0.5f ||
                                  Math.Abs(clean.LowPassHz - options.LowPassHz) > 0.5f;
            options = clean;
            if (filtersChanged) ConfigureFilters(clean);
        }
    }

    public void Reset(bool? stereoSource = null)
    {
        lock (gate)
        {
            if (stereoSource.HasValue) isStereoSource = stereoSource.Value;
            Array.Clear(midAccum);
            Array.Clear(sideAccum);
            Array.Clear(olaBuffer);
            Array.Clear(smoothMid);
            Array.Clear(smoothSide);
            Array.Fill(noiseFloor, 1e-6f);
            Array.Clear(rawDelay);
            rawDelayIndex = 0;
            stftFill = HopSize;
            hp1.Reset();
            hp2.Reset();
            lp1.Reset();
            Array.Clear(rawFirBuf);
            Array.Clear(cleanFirBuf);
            firPos = 0;
            decimatePhase = 0;
        }
    }

    public static DspOptions Sanitize(DspOptions o) => new(
        CenterStrength: Math.Clamp(float.IsFinite(o.CenterStrength) ? o.CenterStrength : 1.2f, 0f, 2.5f),
        HighPassHz: o.HighPassHz <= 0f ? 0f : Math.Clamp(float.IsFinite(o.HighPassHz) ? o.HighPassHz : 80f, 30f, 300f),
        LowPassHz: o.LowPassHz <= 0f ? 0f : Math.Clamp(float.IsFinite(o.LowPassHz) ? o.LowPassHz : 7600f, 3000f, 15000f),
        EnableNoiseGate: o.EnableNoiseGate,
        NoiseGateStrength: Math.Clamp(float.IsFinite(o.NoiseGateStrength) ? o.NoiseGateStrength : 0.5f, 0f, 1f),
        EnableNormalize: o.EnableNormalize);

    public DspBlockResult ProcessStereoBlock(ReadOnlySpan<float> interleavedStereo48k)
    {
        int frames = interleavedStereo48k.Length / 2;
        if (frames == 0)
            return new DspBlockResult([], [], -90f, -90f, 1f);

        lock (gate)
        {
            var rawOut = new List<float>(frames / 3 + 2);
            var cleanOut = new List<float>(frames / 3 + 2);

            double rawSq = 0;
            double cleanSq = 0;
            double cohWeighted = 0;
            int cohFrames = 0;

            for (int f = 0; f < frames; f++)
            {
                float l = interleavedStereo48k[f * 2];
                float r = interleavedStereo48k[f * 2 + 1];
                if (!float.IsFinite(l)) l = 0f;
                if (!float.IsFinite(r)) r = 0f;

                float mid = isStereoSource ? 0.5f * (l + r) : l;
                float side = isStereoSource ? 0.5f * (l - r) : 0f;

                // Delay raw Mid by HopSize=256 so raw16k aligns frame-for-frame with OLA clean16k
                float alignedRaw = rawDelay[rawDelayIndex];
                rawDelay[rawDelayIndex] = mid;
                rawDelayIndex = (rawDelayIndex + 1) % HopSize;

                midAccum[stftFill] = mid;
                sideAccum[stftFill] = side;
                float clean48k = olaBuffer[stftFill - HopSize];
                stftFill++;

                if (stftFill == FftSize)
                {
                    cohWeighted += ProcessStftHop(options, isStereoSource);
                    cohFrames++;
                    Buffer.BlockCopy(midAccum, HopSize * sizeof(float), midAccum, 0, HopSize * sizeof(float));
                    Buffer.BlockCopy(sideAccum, HopSize * sizeof(float), sideAccum, 0, HopSize * sizeof(float));
                    stftFill = HopSize;
                }

                // Filter cleaned path (HPF + LPF)
                if (options.HighPassHz > 0f)
                {
                    clean48k = hp1.Process(clean48k);
                    clean48k = hp2.Process(clean48k);
                }
                if (options.LowPassHz > 0f)
                {
                    clean48k = lp1.Process(clean48k);
                }
                clean48k = Math.Clamp(clean48k, -1.5f, 1.5f);

                rawSq += alignedRaw * (double)alignedRaw;
                cleanSq += clean48k * (double)clean48k;

                // Push into anti-aliased 3:1 FIR decimator
                rawFirBuf[firPos] = alignedRaw;
                cleanFirBuf[firPos] = clean48k;
                firPos = (firPos + 1) % DecimatorFir.Length;

                decimatePhase++;
                if (decimatePhase == 3)
                {
                    decimatePhase = 0;
                    rawOut.Add(EvaluateFir(rawFirBuf, firPos));
                    cleanOut.Add(EvaluateFir(cleanFirBuf, firPos));
                }
            }

            float rawDb = AmplitudeToDb((float)Math.Sqrt(rawSq / frames));
            float cleanDb = AmplitudeToDb((float)Math.Sqrt(cleanSq / frames));
            float coherence = cohFrames > 0 ? (float)(cohWeighted / cohFrames) : 1f;

            return new DspBlockResult(rawOut.ToArray(), cleanOut.ToArray(), rawDb, cleanDb, coherence);
        }
    }

    public static OfflineDspResult ProcessOffline(
        float[] left,
        float[] right,
        int inputSampleRate,
        bool isStereo,
        DspOptions options)
    {
        if (left.Length == 0)
            return new OfflineDspResult([], [], -90f, -90f, 1f);

        int count = Math.Min(left.Length, right.Length);
        var stereo48k = ResampleToInterleaved48k(left.AsSpan(0, count), right.AsSpan(0, count), inputSampleRate, isStereo);

        var pipeline = new AudioDspPipeline(options, isStereo);
        // Append one HopSize of tail silence to flush STFT & filter latency cleanly
        int totalFrames = stereo48k.Length / 2;
        var padded = new float[(totalFrames + HopSize + 24) * 2];
        Array.Copy(stereo48k, padded, stereo48k.Length);

        var res = pipeline.ProcessStereoBlock(padded);
        int expected16k = (int)Math.Floor(totalFrames / 3.0);
        int trimOffset = Math.Min(res.Raw16k.Length, HopSize / 3);
        int take = Math.Min(expected16k, Math.Max(0, res.Raw16k.Length - trimOffset));

        var raw16k = res.Raw16k.AsSpan(trimOffset, take).ToArray();
        var clean16k = res.Clean16k.AsSpan(trimOffset, take).ToArray();

        if (options.EnableNormalize)
        {
            NormalizeSpeech(clean16k);
        }

        return new OfflineDspResult(
            raw16k,
            clean16k,
            ComputeRmsDb(raw16k),
            ComputeRmsDb(clean16k),
            res.CenterCoherence);
    }

    public static void NormalizeSpeech(Span<float> samples, float targetRmsDb = -18f, float maxGainDb = 26f)
    {
        if (samples.Length == 0) return;
        float rmsDb = ComputeRmsDb(samples);
        if (rmsDb < -64f)
        {
            SoftLimit(samples);
            return;
        }

        float gainDb = Math.Clamp(targetRmsDb - rmsDb, -6f, maxGainDb);
        float gain = MathF.Pow(10f, gainDb / 20f);
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }
        SoftLimit(samples);
    }

    public static void SoftLimit(Span<float> samples)
    {
        const float ceiling = 0.94f;
        for (int i = 0; i < samples.Length; i++)
        {
            float x = samples[i];
            if (!float.IsFinite(x)) { samples[i] = 0f; continue; }
            if (MathF.Abs(x) > ceiling)
            {
                samples[i] = MathF.Tanh(x / ceiling) * ceiling;
            }
        }
    }

    public static float ComputeRmsDb(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return -90f;
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double s = samples[i];
            sum += s * s;
        }
        return AmplitudeToDb((float)Math.Sqrt(sum / samples.Length));
    }

    public static float AmplitudeToDb(float rms)
    {
        if (!float.IsFinite(rms) || rms <= 1e-4f) return -80f;
        return Math.Clamp(20f * MathF.Log10(rms), -80f, 0f);
    }

    private float ProcessStftHop(DspOptions opt, bool stereo)
    {
        for (int n = 0; n < FftSize; n++)
        {
            float w = SqrtHannWindow[n];
            fftRe[n] = midAccum[n] * w;
            fftIm[n] = sideAccum[n] * w;
        }

        Fft(fftRe, fftIm, inverse: false);

        double voiceMidPower = 0;
        double voiceSidePower = 0;

        float minFloor = opt.CenterStrength <= 0f
            ? 1f
            : MathF.Max(0.03f, 1f - 0.72f * opt.CenterStrength);

        for (int k = 0; k <= HalfBins; k++)
        {
            int kConj = k == 0 ? 0 : FftSize - k;
            float zr = fftRe[k], zi = fftIm[k];
            float cr = fftRe[kConj], ci = fftIm[kConj];

            // Recover M[k] and S[k] from combined FFT(mid + j*side)
            float mr = k == 0 ? zr : 0.5f * (zr + cr);
            float mi = k == 0 ? 0f : 0.5f * (zi - ci);
            float sr = k == 0 ? zi : 0.5f * (zi + ci);
            float si = k == 0 ? 0f : -0.5f * (zr - cr);

            float instMid = mr * mr + mi * mi;
            float instSide = sr * sr + si * si;

            smoothMid[k] = 0.65f * instMid + 0.35f * smoothMid[k];
            smoothSide[k] = 0.65f * instSide + 0.35f * smoothSide[k];

            // Speech band ~280Hz..3500Hz at 48kHz/512pt (bins 3..37)
            if (k is >= 3 and <= 37)
            {
                voiceMidPower += instMid;
                voiceSidePower += instSide;
            }

            float gain = 1f;
            if (stereo && opt.CenterStrength > 0f)
            {
                float ratio = smoothMid[k] / (smoothMid[k] + opt.CenterStrength * 4.0f * smoothSide[k] + 1e-12f);
                gain = Math.Clamp(MathF.Pow(ratio, 1.2f), minFloor, 1f);
            }

            if (opt.EnableNoiseGate && k >= 1)
            {
                float postPower = instMid * gain * gain;
                if (postPower < noiseFloor[k])
                    noiseFloor[k] = 0.90f * noiseFloor[k] + 0.10f * postPower;
                else
                    noiseFloor[k] = MathF.Min(postPower, noiseFloor[k] * 1.008f + 1e-9f);

                float snr = postPower / (noiseFloor[k] + 1e-12f);
                float gateThresh = 1.6f + 4.4f * opt.NoiseGateStrength;
                if (snr < gateThresh)
                {
                    float ngFloor = MathF.Max(0.08f, 1f - 0.85f * opt.NoiseGateStrength);
                    float ngGain = Math.Clamp(MathF.Sqrt(snr / gateThresh), ngFloor, 1f);
                    gain *= ngGain;
                }
            }

            float outRe = mr * gain;
            float outIm = mi * gain;
            cleanRe[k] = outRe;
            cleanIm[k] = outIm;
            if (k > 0 && k < HalfBins)
            {
                cleanRe[kConj] = outRe;
                cleanIm[kConj] = -outIm;
            }
        }

        Fft(cleanRe, cleanIm, inverse: true);

        // Shift second half of previous OLA buffer forward, add first half of new synthesized block
        for (int n = 0; n < HopSize; n++)
        {
            float synth0 = cleanRe[n] * SqrtHannWindow[n];
            float synth1 = cleanRe[n + HopSize] * SqrtHannWindow[n + HopSize];
            olaBuffer[n] = olaBuffer[n + HopSize] + synth0;
            olaBuffer[n + HopSize] = synth1;
        }

        return stereo
            ? (float)(voiceMidPower / (voiceMidPower + voiceSidePower + 1e-12))
            : 1f;
    }

    private float EvaluateFir(float[] ring, int nextPos)
    {
        float acc = 0f;
        int len = DecimatorFir.Length;
        for (int k = 0; k < len; k++)
        {
            int idx = (nextPos - 1 - k + len) % len;
            acc += ring[idx] * DecimatorFir[k];
        }
        return acc;
    }

    private void ConfigureFilters(DspOptions opt)
    {
        if (opt.HighPassHz > 0f)
        {
            // 4th-order Butterworth cascade for steeper sub-bass explosion suppression
            hp1.ConfigureHighPass(opt.HighPassHz, WorkingSampleRate, q: 0.5411961f);
            hp2.ConfigureHighPass(opt.HighPassHz, WorkingSampleRate, q: 1.3065630f);
        }
        else
        {
            hp1.Reset();
            hp2.Reset();
        }

        if (opt.LowPassHz > 0f && opt.LowPassHz < WorkingSampleRate * 0.45f)
        {
            lp1.ConfigureLowPass(opt.LowPassHz, WorkingSampleRate, q: 0.70710678f);
        }
        else
        {
            lp1.Reset();
        }
    }

    private static float[] ResampleToInterleaved48k(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        int inputSampleRate,
        bool isStereo)
    {
        if (inputSampleRate == WorkingSampleRate)
        {
            var same = new float[left.Length * 2];
            for (int i = 0; i < left.Length; i++)
            {
                same[i * 2] = left[i];
                same[i * 2 + 1] = isStereo ? right[i] : left[i];
            }
            return same;
        }

        double ratio = WorkingSampleRate / (double)inputSampleRate;
        int outFrames = Math.Max(1, (int)Math.Round(left.Length * ratio));
        var res = new float[outFrames * 2];
        for (int i = 0; i < outFrames; i++)
        {
            double srcPos = i / ratio;
            int i0 = (int)Math.Floor(srcPos);
            float frac = (float)(srcPos - i0);
            int i1 = Math.Min(i0 + 1, left.Length - 1);
            i0 = Math.Clamp(i0, 0, left.Length - 1);

            float l = left[i0] + (left[i1] - left[i0]) * frac;
            float r = isStereo ? right[i0] + (right[i1] - right[i0]) * frac : l;
            res[i * 2] = l;
            res[i * 2 + 1] = r;
        }
        return res;
    }

    private static float[] BuildSqrtHannWindow(int n)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++)
        {
            float hann = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / n));
            w[i] = MathF.Sqrt(MathF.Max(0f, hann));
        }
        return w;
    }

    private static float[] BuildLowPassFir(int taps, float cutoffHz, int sampleRate)
    {
        var h = new float[taps];
        int mid = taps / 2;
        float fc = cutoffHz / sampleRate;
        float sum = 0f;
        for (int i = 0; i < taps; i++)
        {
            int k = i - mid;
            float sinc = k == 0 ? 2f * fc : MathF.Sin(2f * MathF.PI * fc * k) / (MathF.PI * k);
            float win = 0.54f - 0.46f * MathF.Cos(2f * MathF.PI * i / (taps - 1));
            h[i] = sinc * win;
            sum += h[i];
        }
        for (int i = 0; i < taps; i++) h[i] /= sum;
        return h;
    }

    private static void Fft(float[] re, float[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        float sign = inverse ? 1f : -1f;
        for (int len = 2; len <= n; len <<= 1)
        {
            float ang = sign * 2f * MathF.PI / len;
            float wLenRe = MathF.Cos(ang);
            float wLenIm = MathF.Sin(ang);
            int half = len >> 1;

            for (int i = 0; i < n; i += len)
            {
                float wRe = 1f;
                float wIm = 0f;
                for (int j = 0; j < half; j++)
                {
                    int u = i + j;
                    int v = u + half;
                    float vr = re[v] * wRe - im[v] * wIm;
                    float vi = re[v] * wIm + im[v] * wRe;

                    re[v] = re[u] - vr;
                    im[v] = im[u] - vi;
                    re[u] += vr;
                    im[u] += vi;

                    float nextWRe = wRe * wLenRe - wIm * wLenIm;
                    wIm = wRe * wLenIm + wIm * wLenRe;
                    wRe = nextWRe;
                }
            }
        }

        if (inverse)
        {
            float scale = 1f / n;
            for (int i = 0; i < n; i++)
            {
                re[i] *= scale;
                im[i] *= scale;
            }
        }
    }

    private struct BiquadFilter
    {
        private float b0 = 1f, b1, b2, a1, a2;
        private float z1, z2;

        public BiquadFilter() { }

        public void Reset() => z1 = z2 = 0f;

        public void ConfigureHighPass(float freqHz, int sampleRate, float q)
        {
            float w0 = 2f * MathF.PI * freqHz / sampleRate;
            float cosW0 = MathF.Cos(w0);
            float sinW0 = MathF.Sin(w0);
            float alpha = sinW0 / (2f * q);
            float invA0 = 1f / (1f + alpha);

            b0 = (1f + cosW0) * 0.5f * invA0;
            b1 = -(1f + cosW0) * invA0;
            b2 = (1f + cosW0) * 0.5f * invA0;
            a1 = -2f * cosW0 * invA0;
            a2 = (1f - alpha) * invA0;
        }

        public void ConfigureLowPass(float freqHz, int sampleRate, float q)
        {
            float w0 = 2f * MathF.PI * freqHz / sampleRate;
            float cosW0 = MathF.Cos(w0);
            float sinW0 = MathF.Sin(w0);
            float alpha = sinW0 / (2f * q);
            float invA0 = 1f / (1f + alpha);

            b0 = (1f - cosW0) * 0.5f * invA0;
            b1 = (1f - cosW0) * invA0;
            b2 = (1f - cosW0) * 0.5f * invA0;
            a1 = -2f * cosW0 * invA0;
            a2 = (1f - alpha) * invA0;
        }

        public float Process(float x)
        {
            float y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return y;
        }
    }
}
