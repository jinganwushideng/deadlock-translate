using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DeadlockVoiceLab.Audio;
using DeadlockVoiceWorker;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var outputLock = new object();
void Output(object value) { lock (outputLock) Console.WriteLine(JsonSerializer.Serialize(value, json)); }
string errorStage = "capture";

try
{
    if (args is ["--list-microphones"])
    {
        Output(new { devices = WasapiProcessCapture.ListMicrophones() });
        return 0;
    }
    if (args is ["--check-cuda", var checkCudaPath])
    {
        try { CudaDependencies.Load(checkCudaPath); Output(new { cuda = "ready" }); }
        catch (CudaDependencyException ex) { Output(new { cuda = ex.Reason }); }
        return 0;
    }

    if (args is ["--benchmark", var model, var device, .. var files] && files.Length > 0)
    {
        var timer = Stopwatch.StartNew();
        using var engine = new VoiceRecognition(model, device);
        await engine.Read(new float[16000], "en", CancellationToken.None);
        Output(new { warmupMs = timer.Elapsed.TotalMilliseconds });
        foreach (var file in files)
        {
            var wav = WavCodec.Decode(File.ReadAllBytes(file));
            if (wav.SampleRate != 16000 || wav.IsStereo) throw new InvalidDataException("Expected mono 16 kHz audio");
            var result = await engine.Read(wav.Left, "auto", CancellationToken.None);
            Output(new { file = Path.GetFileName(file), result });
        }
        return 0;
    }

    // The parent passes only its verified game PID and the selected model. EOF
    // stops capture even if the parent exits without an explicit stop command.
    if (args is not ["--game", var pidValue, var modelFile, var backend, var language, .. var extra] ||
        !int.TryParse(pidValue, out var pid) || extra.Length > 4) return 2;
    string? draftPath = null;
    int draftArgument = Array.IndexOf(extra, "--draft-model");
    if (draftArgument >= 0)
    {
        if (draftArgument + 1 >= extra.Length) return 2;
        draftPath = extra[draftArgument + 1];
        extra = extra.Where((_, index) => index != draftArgument && index != draftArgument + 1).ToArray();
    }
    bool loopback = !extra.Contains("--no-loopback");
    using var workerProcess = Process.GetCurrentProcess();
    void UseCpuPriority()
    {
        try { workerProcess.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (System.ComponentModel.Win32Exception) { }
    }
    if (backend == "cpu") UseCpuPriority();
    using var stop = new CancellationTokenSource();
    using var capture = new WasapiProcessCapture();
    var dsp = new AudioDspPipeline();
    var segmenter = new VoiceSegmenter(SegmenterOptions.TeammateVoice);
    var segments = new CaptionQueue();
    var recovery = new CaptionRecovery();
    var appendRecoveryAudio = (Action<float[]>)recovery.Append;
    var wake = Channel.CreateBounded<bool>(1);
    void Wake() => wake.Writer.TryWrite(true);
    var inferenceGate = new object();
    CancellationTokenSource? teammateInference = null;
    bool teammateInferenceIsPreview = false, recovering = false;
    await using var microphone = new MicrophoneInput(Output, () =>
    {
        lock (inferenceGate) teammateInference?.Cancel();
        Wake();
    }, previewIntervalMs: backend == "cpu" ? 1000 : 600);
    var audioLock = new object();
    const int firstPreviewSamples = AudioDspPipeline.OutputSampleRate * 600 / 1000;
    const int previewStepSamples = AudioDspPipeline.OutputSampleRate * 900 / 1000;
    var draftSchedule = new DraftSchedule();
    double previewEstimateMs = 800;
    long sourceRevision = segmenter.SegmentRevision;
    int nextPreviewSamples = firstPreviewSamples;
    CaptionWork? preview = null;
    long epoch = 0, deadline = 0;
    bool active = false;
    bool tracing = false;
    int utterance = 0, audioFrames = 0, speechFrames = 0, queuedSegments = 0;
    float peak = -120;
    void Trace(string stage, int id, int? audioMs = null, int? speechMs = null, double? levelDb = null,
        int? segments = null, double? elapsedMs = null, double? cpuMs = null)
    {
        if (!Volatile.Read(ref tracing) || id == 0) return;
        Output(new { epoch = Interlocked.Read(ref epoch), voiceTrace = new { stage, utterance = id, audioMs, speechMs, levelDb, segments, elapsedMs, cpuMs } });
    }
    void OpenTrace()
    {
        if (!tracing) return;
        audioFrames = speechFrames = queuedSegments = 0;
        peak = -120;
        Trace("voice-capture-start", utterance);
    }
    void ResetCapture()
    {
        segmenter.Reset();
        segments.Clear();
        recovery.Clear();
        ResetPreview(cancelFinal: true);
        dsp.Reset();
        active = false;
        epoch = 0;
    }
    void ResetPreview(bool cancelFinal = false)
    {
        sourceRevision = segmenter.SegmentRevision;
        nextPreviewSamples = firstPreviewSamples;
        preview = null;
        lock (inferenceGate)
            if (cancelFinal || teammateInferenceIsPreview) teammateInference?.Cancel();
    }
    void Queue(CompletedAudioSegment? segment)
    {
        if (segment is { CleanMono16k.Length: > 0 })
        {
            var work = new CaptionWork(epoch, segment.Revision, segment.CleanMono16k, Environment.TickCount64, utterance, segment.DurationMs);
            var dropped = segments.Enqueue(work, out bool merged);
            if (tracing) Trace("voice-segment", utterance, audioMs: segment.DurationMs, segments: ++queuedSegments);
            if (merged) Trace("voice-queue-merged", utterance);
            if (dropped is not null)
            {
                recovery.Complete(dropped, accepted: false);
                Trace("voice-queue-dropped", dropped.Utterance, dropped.DurationMs, elapsedMs: Environment.TickCount64 - dropped.At);
            }
            Wake();
        }
    }

    errorStage = "engine";
    string? cudaPath = extra.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal));
    bool disableFlash = extra.Contains("--no-flash-attention");
    using var recognizer = new VoiceRecognition(modelFile, backend, cudaPath, disableFlash);
    // Warm the chosen model before taking audio. The parent expires the HUD
    // signal independently and only sends the most recent speaking state.
    var warmupSamples = new float[16000];
    var warmup = await recognizer.Read(warmupSamples, "en", stop.Token, teammateCaption: loopback);
    bool cpuInference = backend == "cpu" || recognizer.ExecutionBackend == "cpu";
    double recentFullMs = cpuInference ? double.PositiveInfinity : warmup.Milliseconds;
    if (backend != "cpu" && recognizer.ExecutionBackend == "cpu") UseCpuPriority();
    bool sharedDraft = draftPath != null && string.Equals(modelFile, draftPath, StringComparison.OrdinalIgnoreCase);
    using var draftRecognizer = loopback && draftPath != null && !sharedDraft
        ? await WarmDraft(draftPath) : null;
    // Native cancellation cannot preempt all CPU kernels. Keep finals moving
    // on machines where even a warmed preview would take too long.
    bool previewsAvailable = !cpuInference || draftRecognizer != null || warmup.Milliseconds < DraftSchedule.BudgetMs;
    async Task<VoiceRecognition?> WarmDraft(string path)
    {
        Output(new { draftLoading = true });
        VoiceRecognition? draft = null;
        try
        {
            draft = new VoiceRecognition(path, backend, cudaPath, disableFlash, reuseRuntime: true);
            var previewWarmup = await draft.Read(warmupSamples, language, stop.Token, preview: true, teammateCaption: true);
            if (!cpuInference && draft.ExecutionBackend == "cpu" ||
                cpuInference && previewWarmup.Milliseconds >= DraftSchedule.BudgetMs)
            {
                draft.Dispose();
                return null;
            }
            return draft;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            draft?.Dispose();
            return null;
        }
        catch (OperationCanceledException)
        {
            draft?.Dispose();
            throw;
        }
    }
    errorStage = "capture";
    if (loopback) await capture.StartAsync("process", pid, (samples, stereo) =>
    {
        lock (audioLock)
        {
            if (deadline == 0 || Environment.TickCount64 > deadline)
            {
                if (deadline != 0) ResetCapture();
                deadline = 0;
                return;
            }
            var captured = segmenter.Feed(samples, dsp, out var meter, allowEarlyManualSplit: segments.IsEmpty,
                captureCleanAudio: appendRecoveryAudio);
            if (sourceRevision != segmenter.SegmentRevision) ResetPreview();
            if (active && float.IsFinite(meter.RawDb))
            {
                if (peak <= -75 && meter.RawDb > -75) Trace("voice-audio", utterance, levelDb: meter.RawDb);
                peak = Math.Max(peak, meter.RawDb);
            }
            if (tracing && active)
            {
                audioFrames += samples.Length / 2;
                if (meter.VadTriggered) speechFrames += samples.Length / 2;
            }
            foreach (var segment in captured)
                Queue(segment);
            if (active && previewsAvailable && !draftSchedule.Paused && segments.IsEmpty &&
                segmenter.SnapshotPreview(nextPreviewSamples, minimumSpeechMs: 600,
                    minimumTimeBeforeSplitMs: previewEstimateMs) is { } snapshot)
            {
                nextPreviewSamples = snapshot.Length + previewStepSamples;
                int durationMs = snapshot.Length * 1000 / AudioDspPipeline.OutputSampleRate;
                preview = new(epoch, sourceRevision, snapshot, Environment.TickCount64, utterance, durationMs, Preview: true);
                Wake();
            }
        }
    }, error => { Output(new { error, errorStage = "capture" }); stop.Cancel(); }, stop.Token);
    errorStage = "recognition";
    Output(new
    {
        ready = true, executionBackend = recognizer.ExecutionBackend,
        draftMode = draftPath == null ? "single" :
            !previewsAvailable ? "fallback" : sharedDraft ? "shared" : draftRecognizer != null ? "base" : "fallback"
    });
    var input = Task.Run(async () =>
    {
        try
        {
            while (await Console.In.ReadLineAsync(stop.Token) is { } line)
            {
                Signal? signal;
                try { signal = JsonSerializer.Deserialize<Signal>(line, json); }
                catch (JsonException)
                {
                    Output(new { error = "Invalid voice control frame", errorStage = "control" });
                    return;
                }
                await microphone.Update(signal?.Mic, stop.Token);
                lock (audioLock)
                {
                    if (signal == null || !signal.Available)
                    {
                        if (deadline != 0) ResetCapture();
                        deadline = 0;
                        tracing = false;
                        continue;
                    }
                    if (epoch != signal.Epoch)
                    {
                        ResetCapture();
                        epoch = signal.Epoch;
                    }
                    deadline = Environment.TickCount64 + 1150;
                    recovery.Expire(Environment.TickCount64);
                    bool startingTrace = signal.Trace && !tracing;
                    tracing = signal.Trace;
                    if (startingTrace && active && signal.Speaking) OpenTrace();
                    if (active && signal.Speaking && signal.Utterance > 0 && signal.Utterance != utterance)
                    {
                        Queue(segmenter.SetManualGate(false));
                        recovery.End(signal.PreviousEndedAt ?? Environment.TickCount64, audible: peak > -75);
                        ResetPreview();
                        active = false;
                    }
                    if (active == signal.Speaking) continue;
                    active = signal.Speaking;
                    if (active)
                    {
                        utterance++;
                        if (signal.Utterance > 0) utterance = signal.Utterance;
                        recovery.Begin(epoch, utterance);
                        lock (inferenceGate)
                            if (recovering) teammateInference?.Cancel();
                        draftSchedule.BeginUtterance(segments.IsEmpty);
                        peak = -120;
                        OpenTrace();
                    }
                    Queue(segmenter.SetManualGate(active));
                    ResetPreview();
                    if (!active)
                    {
                        recovery.End(signal.EndedAt ?? Environment.TickCount64, audible: peak > -75);
                        Wake();
                    }
                    if (!active && tracing)
                    {
                        var stage = peak <= -75 ? "voice-no-audio" : queuedSegments == 0 ? "voice-no-segment" : "voice-capture-end";
                        Trace(stage, utterance, audioFrames / 48, speechFrames / 48, peak, queuedSegments);
                    }
                }
            }
        }
        finally { stop.Cancel(); }
    });
    try
    {
        await foreach (var _ in wake.Reader.ReadAllAsync(stop.Token))
        {
            while (!stop.IsCancellationRequested)
            {
                if (microphone.TryRead(out var micWork) && micWork is not null)
                {
                    await microphone.Recognize(micWork, recognizer, stop.Token);
                    continue;
                }
                using var lowerPriority = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                CaptionWork? segment;
                lock (audioLock)
                {
                    segment = segments.Take(newestFirst: true);
                    if (segment == null)
                    {
                        segment = preview;
                        preview = null;
                    }
                    segment ??= recovery.Take(Environment.TickCount64, long.MaxValue, recentFullMs, active);
                    if (segment is null) break;
                    if (segment.Epoch != epoch || !recovery.Contains(segment.Utterance)) continue;
                    lock (inferenceGate)
                    {
                        teammateInference = lowerPriority;
                        teammateInferenceIsPreview = segment.Preview;
                        recovering = segment.Recovery;
                    }
                }
                long inferenceStarted = Stopwatch.GetTimestamp();
                bool inferenceRan = false;
                try
                {
                    long waitMs = Environment.TickCount64 - segment.At;
                    if (waitMs > (segment.Preview ? 1100 : 5000))
                    {
                        lock (audioLock) recovery.Complete(segment, accepted: false);
                        if (!segment.Preview) Trace("voice-asr-expired", segment.Utterance, segment.DurationMs, elapsedMs: waitMs);
                        continue;
                    }
                    if (microphone.HasPending)
                    {
                        lock (audioLock) recovery.Complete(segment, accepted: false);
                        if (!segment.Preview) Trace("voice-asr-discarded", segment.Utterance, segment.DurationMs);
                        continue;
                    }
                    if (segment.Preview) lowerPriority.CancelAfter(DraftSchedule.BudgetMs);
                    if (segment.Recovery)
                    {
                        lowerPriority.CancelAfter(segment.BudgetMs);
                        AudioDspPipeline.NormalizeSpeech(segment.Samples);
                    }
                    Trace(segment.Recovery ? "voice-retry-start" : segment.Preview ? "voice-preview-start" : "voice-asr-start",
                        segment.Utterance, segment.DurationMs, elapsedMs: waitMs);
                    double? cpuStarted = Volatile.Read(ref tracing) ? workerProcess.TotalProcessorTime.TotalMilliseconds : null;
                    var engine = segment.Preview && draftRecognizer != null ? draftRecognizer : recognizer;
                    inferenceRan = true;
                    var result = await engine.Read(segment.Samples, language, lowerPriority.Token,
                        preview: segment.Preview, teammateCaption: true, recovery: segment.Recovery);
                    double? cpuMs = cpuStarted is double started ? workerProcess.TotalProcessorTime.TotalMilliseconds - started : null;
                    lock (audioLock)
                    {
                        if (!cpuInference && !segment.Preview && !segment.Recovery) recentFullMs = result.Milliseconds;
                        if (lowerPriority.IsCancellationRequested || segment.Epoch != epoch ||
                            segment.Preview && (!active || sourceRevision != segment.Part))
                        {
                            if (segment.Recovery && segment.Epoch == epoch)
                                Trace("voice-retry-canceled", segment.Utterance, segment.DurationMs, elapsedMs: result.Milliseconds);
                            if (segment.Epoch == epoch) recovery.Complete(segment, accepted: false);
                            continue;
                        }
                        bool fresh = recovery.IsInWindow(segment.Utterance, Environment.TickCount64);
                        recovery.Complete(segment, accepted: fresh && result.Trusted && result.Text.Length > 0);
                        if (!fresh)
                            Trace("voice-asr-expired", segment.Utterance, segment.DurationMs, elapsedMs: Environment.TickCount64 - segment.At, cpuMs: cpuMs);
                        else if (result.Trusted && result.Text.Length > 0)
                            Output(new { epoch = segment.Epoch, segment.Part, segment.Preview, segment.Recovery, result,
                                utterance = segment.Utterance, audioMs = segment.DurationMs, executionBackend = recognizer.ExecutionBackend, cpuMs });
                        else if (segment.Recovery)
                            Trace("voice-retry-unconfirmed", segment.Utterance, segment.DurationMs, elapsedMs: result.Milliseconds, cpuMs: cpuMs);
                        else if (result.Text.Length > 0)
                            Trace("voice-asr-unclear", segment.Utterance, segment.DurationMs, elapsedMs: result.Milliseconds, cpuMs: cpuMs);
                        else if (!segment.Preview)
                            Trace("voice-asr-empty", segment.Utterance, segment.DurationMs, elapsedMs: result.Milliseconds, cpuMs: cpuMs);
                    }
                }
                catch (OperationCanceledException) when (lowerPriority.IsCancellationRequested)
                {
                    lock (audioLock)
                    {
                        if (segment.Epoch == epoch) recovery.Complete(segment, accepted: false);
                        if (segment.Recovery) recentFullMs = double.PositiveInfinity;
                    }
                    if (!segment.Preview)
                        Trace(segment.Recovery ? "voice-retry-canceled" : "voice-asr-discarded",
                            segment.Utterance, segment.DurationMs);
                }
                finally
                {
                    double elapsedMs = Stopwatch.GetElapsedTime(inferenceStarted).TotalMilliseconds;
                    if (inferenceRan && segment.Recovery && elapsedMs >= segment.BudgetMs)
                        recentFullMs = double.PositiveInfinity;
                    if (inferenceRan && segment.Preview && (!lowerPriority.IsCancellationRequested || elapsedMs >= DraftSchedule.BudgetMs))
                    {
                        lock (audioLock)
                        {
                            draftSchedule.Observe(elapsedMs);
                            previewEstimateMs = Math.Clamp((previewEstimateMs + elapsedMs) / 2, 350, DraftSchedule.BudgetMs);
                            if (draftSchedule.Paused) preview = null;
                        }
                    }
                    lock (inferenceGate)
                    {
                        teammateInference = null;
                        teammateInferenceIsPreview = false;
                        recovering = false;
                    }
                }
            }
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    finally { stop.Cancel(); await input; }
    return 0;
}
catch (Exception ex)
{
    Output(new { error = ex.Message, errorStage, setupError = ex is CudaDependencyException,
        cuda = (ex as CudaDependencyException)?.Reason });
    return 1;
}

internal sealed record Signal(bool Available, bool Speaking, long Epoch, MicCommand? Mic = null, bool Trace = false,
    int Utterance = 0, long? EndedAt = null, long? PreviousEndedAt = null);
