using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DeadlockTranslate;

internal sealed class VoiceService : IAsyncDisposable
{
    // The worker reads exactly one JSON message per line.
    private static readonly JsonSerializerOptions WorkerJson = new(JsonSerializerDefaults.Web);
    private const int SignalFreshMs = 1250;
    private const int WorkerRetentionMs = 6000;
    private readonly object gate = new();
    private readonly ConfigStore config;
    private readonly Translators translators;
    private readonly DiagnosticLog audit;
    private readonly string gameExecutable;
    private readonly string optionsFile;
    private readonly string workerFile;
    private readonly NativeHotkey? hotkey;
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim speechChanged = new(0, 1);
    private readonly VoiceLatency latency = new();
    private Task? supervisor;
    private VoiceOptions options = new();
    private string session = "";
    private long epoch, seenAt, captionId;
    private bool available, speaking, matchActive;
    private int workerPid;
    private int utterance;
    private CaptionTurn? currentTurn, previousTurn;
    private string status = "off";
    private string? activeBackend;
    private string? executionBackend;
    private string? draftMode;
    private string? problem;
    private bool failed;
    private string? testStage, testHud;
    private long testAt, workerStartedAt;
    private VoiceTrace? testEvidence;
    private double? testElapsed;

    private sealed class CaptionTurn(int utterance)
    {
        public int Utterance { get; } = utterance;
        public long? EndedAt { get; set; }
        public VoiceCaption? Caption { get; set; }
        public long CaptionAt { get; set; }
        public long Part { get; set; }
        public bool DraftTranslationStarted { get; set; }
        public string? TranslatedSource { get; set; }
        public string? TranslatedLanguage { get; set; }
        public CancellationTokenSource? TranslationStop { get; set; }
        public Task<string?>? Translation { get; set; }
        public CaptionTranslation? Pending { get; set; }
        public long Deadline => EndedAt is long end ? end + VoiceCaptionTiming.RecoveryWindowMs : long.MaxValue;

        public void StopTranslation()
        {
            if (TranslationStop != null) _ = RetireTranslation(TranslationStop, Translation);
            TranslationStop = null;
            Translation = null;
            TranslatedSource = TranslatedLanguage = null;
            Pending = null;
        }

        public VoiceCaption? Visible(long now, bool previous)
        {
            if (Caption is not { } caption || now >= Deadline || now >= CaptionAt + 7500) return null;
            if (EndedAt is not long end) return caption;
            return caption with
            {
                RemainingMs = (int)(Deadline - now),
                AgeSeconds = previous ? (int)((now - end) / 1000) : null
            };
        }
    }

    private sealed record CaptionTranslation(string Original, string? Language, Settings Settings, string Mode,
        long Generation, long CaptionId, VoiceTrace Evidence, CancellationToken WorkerToken, bool Recovery);

    public VoiceService(string state, string runtime, string gameExecutable, ConfigStore config, Translators translators, DiagnosticLog audit, NativeHotkey? hotkey)
    {
        this.config = config;
        this.translators = translators;
        this.audit = audit;
        this.gameExecutable = gameExecutable;
        this.hotkey = hotkey;
        optionsFile = Path.Combine(state, "voice-settings.json");
        workerFile = Path.Combine(runtime, "voice", "DeadlockVoiceWorker.exe");
        Models = new VoiceModels(Path.Combine(state, "voice-models"));
        Cuda = new VoiceCudaRuntime(Path.Combine(state, "voice-cuda"), workerFile, RestartCuda);
        try
        {
            if (File.Exists(optionsFile))
                options = ((JsonSerializer.Deserialize<VoiceOptions>(File.ReadAllBytes(optionsFile), ConfigStore.Json) ?? new())
                    with { TestMode = false }).Validated();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or JsonException or UnauthorizedAccessException)
        {
            problem = "语音设置无法读取，已使用默认选项。";
        }
    }

    public VoiceModels Models { get; }
    public VoiceCudaRuntime Cuda { get; }
    private bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) && WorkerAvailable;
    private bool WorkerAvailable => OperatingSystem.IsWindows() && File.Exists(workerFile);

    public async Task<JsonElement> Microphones(CancellationToken ct)
    {
        if (!WorkerAvailable) return JsonSerializer.SerializeToElement(Array.Empty<object>());
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var start = new ProcessStartInfo(workerFile, "--list-microphones")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8
        };
        using var worker = Process.Start(start) ?? throw new IOException("无法读取麦克风列表。");
        try
        {
            var output = await worker.StandardOutput.ReadLineAsync(deadline.Token);
            await worker.WaitForExitAsync(deadline.Token);
            if (worker.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) throw new IOException("无法读取麦克风列表。");
            using var response = JsonDocument.Parse(output);
            return response.RootElement.GetProperty("devices").Clone();
        }
        finally { if (!worker.HasExited) worker.Kill(); }
    }

    public object Status(bool checkCuda = false)
    {
        lock (gate)
            return new
            {
                settings = options,
                status, problem, activeBackend, executionBackend, draftMode, supported = Supported,
                models = VoiceModels.Presets.Select(item => new { item.Id, item.Name, item.Bytes, installed = Models.Installed(item) }).ToArray(),
                download = Models.Status(),
                cuda = Cuda.Status(Supported && (checkCuda || options.Backend == "cuda"))
            };
    }

    public void Save(VoiceOptions value)
    {
        lock (gate) ApplyOptions(value);
    }

    public object Update(VoiceOptionsUpdate update)
    {
        lock (gate)
        {
            ApplyOptions(options with
            {
                Enabled = update.Enabled ?? options.Enabled,
                Model = update.Model ?? options.Model,
                CustomPath = update.CustomPath ?? options.CustomPath,
                Backend = update.Backend ?? options.Backend,
                Language = update.Language ?? options.Language,
                Translation = update.Translation ?? options.Translation,
                TestMode = update.TestMode ?? options.TestMode,
                LightweightDraft = update.LightweightDraft ?? options.LightweightDraft
            });
            return Status();
        }
    }

    private void ApplyOptions(VoiceOptions value)
    {
        value = value.Validated();
        if (value != options)
        {
            bool testChanged = value.TestMode != options.TestMode;
            bool restart = value != (options with { TestMode = value.TestMode });
            // The diagnostic HUD is opt-in for the current backend session.
            if (restart)
                ConfigStore.WriteAtomic(optionsFile, JsonSerializer.SerializeToUtf8Bytes(value with { TestMode = false }, ConfigStore.Json));
            options = value;
            if (restart) Reset();
            if (testChanged)
            {
                testStage = testHud = null;
                ReportTest("voice-test-on", visible: false);
            }
        }
        if (status == "error" || status == "off" || status == "unavailable" || !options.Enabled || !WorkerWanted)
            status = !options.Enabled ? "off" : Supported ? "waiting" : "unavailable";
        failed = false;
        problem = null;
        if (options.Enabled) supervisor ??= Task.Run(Run);
    }

    public object? Exchange(string gameSession, bool compatible, bool? teammateSpeaking, bool inMatch,
        VoiceShown[]? shown = null)
    {
        lock (gate)
        {
            if (session != gameSession)
            {
                session = gameSession;
                Reset();
                failed = false;
                problem = null;
            }
            var enabled = compatible && options.Enabled && config.Current.Enabled && Supported;
            var hasHud = enabled && teammateSpeaking.HasValue;
            var nextMatch = compatible && inMatch;
            bool nextSpeaking = teammateSpeaking == true;
            bool wasFresh = SignalFresh;
            bool changed = !wasFresh || available != hasHud || speaking != nextSpeaking;
            if (!wasFresh || !hasHud) ClearCaptionTurns();
            var now = Environment.TickCount64;
            if (options.TestMode && compatible && shown is { Length: <= 8 })
                foreach (var receipt in shown)
                    if (receipt != null) latency.Confirm(receipt, now, ReportLatency);
            if (!nextMatch && !hasHud && (available || matchActive)) Reset();
            if (hasHud && nextSpeaking && (!wasFresh || !available || !speaking))
            {
                latency.End(utterance, now);
                if (currentTurn != null) currentTurn.EndedAt ??= now;
                previousTurn?.StopTranslation();
                previousTurn = currentTurn;
                utterance++;
                currentTurn = new(utterance);
                if (options.TestMode) latency.Start(utterance, now);
            }
            else if ((!hasHud || !nextSpeaking) && speaking)
            {
                latency.End(utterance, now);
                if (currentTurn != null) currentTurn.EndedAt ??= now;
            }
            if (currentTurn is { } current && now >= current.Deadline) current.StopTranslation();
            if (previousTurn is { } previous && now >= previous.Deadline)
            {
                previous.StopTranslation();
                previousTurn = null;
            }
            latency.Collect(now, ReportLatency);
            available = hasHud;
            matchActive = nextMatch;
            speaking = nextSpeaking;
            seenAt = now;
            if (hasHud && previousTurn is { Pending: { } pending } earlier &&
                currentTurn is { } latest && !speaking && (latest.Caption?.Final == true || pending.Recovery) &&
                (latest.Translation == null || latest.Translation.IsCompleted))
                StartCaptionTranslation(earlier, pending);
            if (options.TestMode && enabled)
            {
                var currentHud = !hasHud ? "voice-hud-wait" : speaking ? "voice-detected" : "voice-hud-idle";
                if (currentHud != testHud)
                {
                    if (currentHud == "voice-hud-idle" && testHud == "voice-detected")
                        ReportTest("voice-stopped", visible: testStage is "voice-detected" or "voice-capture-start" or "voice-audio");
                    else ReportTest(currentHud);
                    testHud = currentHud;
                }
            }
            if (changed && speechChanged.CurrentCount == 0) speechChanged.Release();
            hotkey?.Dictation?.Observe(gameSession, nextMatch, hotkey.IsForegroundProcess(workerPid));
            if (enabled || WorkerAvailable && nextMatch && hotkey is not null && VoiceDictation.CanUse(config.Current))
                supervisor ??= Task.Run(Run);
            var debug = options.TestMode && (inMatch || teammateSpeaking.HasValue) ? TestView(compatible, enabled) : null;
            var visible = hasHud ? currentTurn?.Visible(now, previous: false) : null;
            var late = hasHud ? previousTurn?.Visible(now, previous: true) : null;
            if (options.TestMode && visible != null) latency.Issue(visible);
            if (options.TestMode && late != null) latency.Issue(late);
            return enabled || debug is not null ? new { enabled, caption = visible, previous = late, debug, measureHud = options.TestMode } : null;
        }
    }

    private void Reset(bool keepHud = false)
    {
        latency.Collect(Environment.TickCount64, ReportLatency, reset: true);
        epoch++;
        testStage = testHud = null;
        testEvidence = null;
        if (!keepHud) { available = false; speaking = false; matchActive = false; }
        hotkey?.Dictation?.Cancel();
        ClearCaptionTurns();
        activeBackend = null;
        executionBackend = null;
        draftMode = null;
        if (status is "ready" or "loading") status = "waiting";
    }

    private void ClearCaptionTurns()
    {
        currentTurn?.StopTranslation();
        previousTurn?.StopTranslation();
        currentTurn = previousTurn = null;
    }

    private void RestartCuda()
    {
        lock (gate)
        {
            if ((!options.Enabled && !VoiceDictation.CanUse(config.Current)) || options.Backend != "cuda") return;
            Reset(keepHud: true);
            failed = false;
            problem = null;
            status = "waiting";
            if (speechChanged.CurrentCount == 0) speechChanged.Release();
        }
    }

    // A short bridge interruption closes the audio gate without unloading the model.
    private bool SignalFresh => Environment.TickCount64 - seenAt < SignalFreshMs;
    private bool TeammateSignal => SignalFresh && available && options.Enabled && config.Current.Enabled;
    private bool WorkerWanted
    {
        get
        {
            if (Environment.TickCount64 - seenAt >= WorkerRetentionMs) return false;
            var settings = config.Current;
            var teammateCaptions = (matchActive || available) && options.Enabled && settings.Enabled && Supported;
            var dictation = matchActive && WorkerAvailable && hotkey is not null && VoiceDictation.CanUse(settings);
            return teammateCaptions || dictation;
        }
    }

    private async Task Run()
    {
        var ct = shutdown.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                VoiceOptions selected;
                long generation;
                bool start;
                lock (gate)
                {
                    selected = options;
                    generation = epoch;
                    start = SignalFresh && WorkerWanted && !failed;
                    if (!start) status = failed ? "error" : !selected.Enabled ? "off" : !Supported ? "unavailable" : "waiting";
                }
                if (start)
                {
                    try { await RunWorker(selected, generation, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException)
                    {
                        lock (gate)
                        {
                            if (generation == epoch)
                            {
                                failed = true;
                                problem = ex is IOException ? ex.Message : "语音引擎启动失败，请核对模型和识别设备。";
                                hotkey?.Dictation?.Fail(problem);
                                ReportTest("voice-worker-error", failure: ex.GetType().Name,
                                    visible: testStage is not ("voice-control-error" or "voice-capture-error" or "voice-engine-error" or "voice-asr-error"));
                                activeBackend = null;
                                status = "error";
                                audit.Record("backend", LogKind.VoiceState, new(Stage: "failed"));
                            }
                        }
                    }
                }
                await Task.Delay(150, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task RunWorker(VoiceOptions selected, long generation, CancellationToken ct)
    {
        var pid = FindGame();
        lock (gate) workerPid = pid;
        var model = await Models.Verify(selected, ct);
        string? draft = null;
        if (selected.Enabled && selected.LightweightDraft)
        {
            try
            {
                draft = selected.Model == VoiceModels.DraftModel ? model : await Models.VerifyPreset(VoiceModels.DraftModel, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Optional model. The selected recognizer remains available.
            }
        }
        (string Device, bool DisableFlash)[] devices = selected.Backend switch
        {
            "cuda" => [("cuda", false), ("vulkan", false), ("vulkan", true), ("cpu", false)],
            "vulkan" => [("vulkan", false), ("vulkan", true)],
            _ => [(selected.Backend, false)]
        };
        for (var i = 0; i < devices.Length; i++)
        {
            var (device, disableFlash) = devices[i];
            try
            {
                try
                {
                    await RunDevice(selected, device, disableFlash, pid, model, draft, generation, ct);
                }
                catch (VoiceEngineStartupException) when (draft != null && DraftWasLoading(generation))
                {
                    draft = null;
                    lock (gate) ReportTest("voice-draft-fallback", visible: false);
                    await RunDevice(selected, device, disableFlash, pid, model, null, generation, ct);
                }
                return;
            }
            catch (VoiceEngineStartupException) when (i + 1 < devices.Length)
            {
                lock (gate)
                {
                    if (!WorkerWanted || generation != epoch) return;
                    string stage = device == "cuda" ? "voice-cuda-fallback" :
                        disableFlash ? "voice-vulkan-fallback" : "voice-vulkan-standard";
                    audit.Record("backend", LogKind.VoiceState, new(Stage: stage));
                }
            }
        }
    }

    private bool DraftWasLoading(long generation)
    {
        lock (gate) return generation == epoch && WorkerWanted && draftMode is ("loading" or "base");
    }

    private async Task RunDevice(VoiceOptions selected, string device, bool disableFlash, int pid, string model,
        string? draft, long generation, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = lifetime.Token;
        lock (gate)
        {
            if (!WorkerWanted || generation != epoch) return;
            status = "loading";
            activeBackend = device;
            executionBackend = null;
            draftMode = null;
            workerStartedAt = Environment.TickCount64;
            ReportTest("voice-loading");
        }
        var start = new ProcessStartInfo(workerFile)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "--game", pid.ToString(), model, device, selected.Language })
            start.ArgumentList.Add(argument);
        if (device == "cuda") start.ArgumentList.Add(Cuda.DirectoryPath);
        if (disableFlash) start.ArgumentList.Add("--no-flash-attention");
        if (!selected.Enabled || !Supported) start.ArgumentList.Add("--no-loopback");
        else if (draft != null)
        {
            start.ArgumentList.Add("--draft-model");
            start.ArgumentList.Add(draft);
        }
        using var worker = Process.Start(start) ?? throw new IOException("无法启动语音引擎。");
        bool fastVulkan = device == "vulkan" && !disableFlash;
        var output = ReadWorker(worker, generation, selected, fastVulkan || draft != null, token);
        var errors = worker.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        var started = Environment.TickCount64;
        bool signalPaused = false;
        try
        {
            while (!worker.HasExited && !output.IsCompleted)
            {
                bool open;
                bool ready;
                bool teammate;
                bool trace;
                int speakingId;
                long? endedAt;
                long? previousEndedAt;
                DictationSignal? mic;
                lock (gate)
                {
                    if (!WorkerWanted || generation != epoch) return;
                    if (status == "loading" && Environment.TickCount64 - started > 90000)
                        throw new IOException("语音模型加载超时，请检查所选设备或模型。");
                    bool fresh = SignalFresh;
                    bool pause = !fresh;
                    if (pause != signalPaused)
                    {
                        signalPaused = pause;
                        if (signalPaused) ClearCaptionTurns();
                        ReportTest(signalPaused ? "voice-signal-paused" : "voice-signal-resumed", visible: false);
                    }
                    teammate = TeammateSignal;
                    open = teammate && speaking;
                    trace = teammate && options.TestMode;
                    speakingId = open ? utterance : 0;
                    endedAt = currentTurn?.EndedAt;
                    previousEndedAt = previousTurn?.EndedAt;
                    ready = status == "ready";
                    mic = hotkey?.Dictation?.Poll(ready, fresh && matchActive && hotkey.IsForegroundProcess(pid));
                }
                if (!ready) { await Task.Delay(120, token); continue; }
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                writeTimeout.CancelAfter(1500);
                var pulse = FormatWorkerPulse(teammate, open, generation, mic, trace, speakingId, endedAt, previousEndedAt);
                await worker.StandardInput.WriteLineAsync(pulse.AsMemory(), writeTimeout.Token);
                await worker.StandardInput.FlushAsync(writeTimeout.Token);
                await speechChanged.WaitAsync(120, token);
            }
            await output.WaitAsync(token);
            lock (gate)
            {
                if (!WorkerWanted || generation != epoch) return;
                if (status == "loading" || fastVulkan || draftMode == "base")
                    throw new VoiceEngineStartupException("识别设备意外退出，请检查驱动或切换识别设备。");
            }
            throw new IOException("语音引擎已停止，请检查所选设备或模型。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("语音引擎未响应，请重试。");
        }
        catch (IOException ex) when ((fastVulkan || draft != null) && ex is not VoiceEngineStartupException && worker.HasExited && !output.IsFaulted)
        {
            throw new VoiceEngineStartupException("识别设备意外退出，正在切换到 Vulkan 兼容模式。");
        }
        finally
        {
            lock (gate)
            {
                if (generation == epoch && !WorkerWanted) Reset();
            }
            lifetime.Cancel();
            try
            {
                worker.StandardInput.Close();
                using var grace = new CancellationTokenSource(350);
                try { await worker.WaitForExitAsync(grace.Token); }
                catch (OperationCanceledException) { if (!worker.HasExited) worker.Kill(); }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await output; } catch (Exception) { /* Observed by the main loop or cancelled on exit. */ }
            try { await errors; } catch (OperationCanceledException) { } catch (IOException) { }
        }
    }

    internal static string FormatWorkerPulse(bool available, bool speaking, long epoch, DictationSignal? mic, bool trace = false,
        int utterance = 0, long? endedAt = null, long? previousEndedAt = null) =>
        JsonSerializer.Serialize(new { available, speaking, epoch, mic, trace, utterance, endedAt, previousEndedAt }, WorkerJson);

    private void ReportLatency(string stage, int id, long elapsed) =>
        ReportTest(stage, new VoiceTrace(Utterance: id), elapsed, visible: false);

    private void ReportTest(string stage, VoiceTrace? evidence = null, double? elapsed = null, int? count = null,
        string? provider = null, LogReason reason = LogReason.None, string? failure = null, bool visible = true)
    {
        // Callers hold the voice gate. Only the structured, allow-listed fields enter the log.
        if (!options.TestMode) return;
        var data = ((evidence ?? new VoiceTrace()) with
        {
            Device = activeBackend ?? options.Backend, Model = options.Model, ExecutionBackend = executionBackend
        }).Clean();
        audit.Record("backend", LogKind.VoiceState, new(Reason: reason, Stage: stage, Provider: provider,
            FailureType: failure, Count: count, ElapsedMs: elapsed, Voice: data));
        if (!visible) return;
        testStage = stage;
        testAt = Environment.TickCount64;
        testEvidence = data;
        testElapsed = elapsed;
    }

    private object TestView(bool compatible, bool enabled)
    {
        var stage = !compatible ? "voice-incompatible" : !enabled ? "voice-disabled" :
            status == "loading" ? "voice-loading" : status == "error" ? (testStage ?? "voice-worker-error") :
            !available ? "voice-hud-wait" :
            testStage is not null && (speaking || seenAt - testAt < 4500) ? testStage : "voice-hud-idle";
        var evidence = stage == testStage ? testEvidence : null;
        return new { stage, speaking = enabled && available && speaking, device = activeBackend ?? evidence?.Device ?? options.Backend, executionBackend,
            model = options.Model, audioMs = evidence?.AudioMs, levelDb = evidence?.LevelDb, elapsedMs = stage == testStage ? testElapsed : null };
    }

    private void ReadExecutionBackend(JsonElement root)
    {
        if (root.TryGetProperty("executionBackend", out var engine) && engine.ValueKind == JsonValueKind.String)
        {
            var value = engine.GetString();
            if (value is "vulkan" or "cuda" or "cpu") executionBackend = value;
        }
    }

    private async Task ReadWorker(Process worker, long generation, VoiceOptions selected, bool retryRecognition, CancellationToken ct)
    {
        try
        {
            while (await worker.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.Length > 8192 || !line.StartsWith('{')) continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("micId", out var micId))
                {
                    lock (gate)
                        if (generation == epoch && matchActive && hotkey?.IsForegroundProcess(workerPid) == true)
                            hotkey.Dictation?.Receive(micId.GetString() ?? "", root.GetProperty("micState").GetString() ?? "",
                                root.TryGetProperty("text", out var micText) ? micText.GetString() : null);
                    continue;
                }
                if (root.TryGetProperty("error", out var error))
                {
                    var stage = root.TryGetProperty("errorStage", out var value) ? value.GetString() : null;
                    lock (gate)
                        if (generation == epoch)
                            ReportTest(stage switch
                            {
                                "control" => "voice-control-error", "capture" => "voice-capture-error",
                                "engine" => "voice-engine-error", _ => "voice-asr-error"
                            });
                    if (root.TryGetProperty("setupError", out var setup) && setup.ValueKind == JsonValueKind.True)
                    {
                        lock (gate)
                            if (generation == epoch && root.TryGetProperty("cuda", out var dependency))
                                Cuda.ReportDependency(dependency.GetString());
                        throw new VoiceEngineStartupException(error.GetString() ?? "CUDA 运行库不可用。");
                    }
                    if (stage == "engine")
                        throw new VoiceEngineStartupException("无法加载或预热语音模型，请核对模型和识别设备。");
                    if (stage == "recognition" && retryRecognition)
                        throw new VoiceEngineStartupException("语音识别中断，正在尝试恢复。");
                    throw new IOException(stage == "capture" ? "无法采集游戏音频，请检查游戏和音频设备。"
                        : stage == "control" ? "语音进程控制消息解析失败。" : "语音识别中断，请核对模型、驱动和音频设备。");
                }
                if (root.TryGetProperty("draftLoading", out _))
                {
                    lock (gate)
                        if (generation == epoch) draftMode = "loading";
                    continue;
                }
                if (root.TryGetProperty("ready", out _))
                {
                    lock (gate)
                    {
                        if (generation != epoch) continue;
                        ReadExecutionBackend(root);
                        var mode = root.TryGetProperty("draftMode", out var value) ? value.GetString() : null;
                        draftMode = mode is "shared" or "base" ? mode : selected.LightweightDraft ? "fallback" : "single";
                        if (draftMode == "fallback") ReportTest("voice-draft-fallback", visible: false);
                        status = "ready";
                        ReportTest("voice-ready", elapsed: Environment.TickCount64 - workerStartedAt);
                        audit.Record("backend", LogKind.VoiceState, new(Stage: "ready"));
                    }
                    continue;
                }
                if (root.TryGetProperty("voiceTrace", out var traceValue))
                {
                    lock (gate)
                    {
                        if (options.TestMode && generation == epoch && root.TryGetProperty("epoch", out var traceEpoch) && traceEpoch.GetInt64() == generation &&
                            traceValue.Deserialize<VoiceWorkerTrace>(WorkerJson) is { Known: true } trace)
                        {
                            bool audible = trace.Stage == "voice-audio" ||
                                trace.Stage is ("voice-capture-end" or "voice-no-segment") && trace.AudioMs > 0;
                            if (audible && trace.LevelDb > -75 && trace.Utterance is int heard && latency.Audio(heard))
                                ReportTest("voice-hud-audible", trace.Evidence, visible: false);
                            ReportTest(trace.Stage, trace.Evidence, trace.ElapsedMs, visible: trace.Stage != "voice-capture-end");
                        }
                    }
                    continue;
                }
                if (!root.TryGetProperty("epoch", out var id) || id.GetInt64() != generation ||
                    !root.TryGetProperty("result", out var result)) continue;
                long part = root.TryGetProperty("part", out var partValue) && partValue.TryGetInt64(out var parsedPart) ? parsedPart : 0;
                bool preview = root.TryGetProperty("preview", out var previewValue) && previewValue.ValueKind == JsonValueKind.True;
                bool recovered = root.TryGetProperty("recovery", out var recoveryValue) && recoveryValue.ValueKind == JsonValueKind.True;
                if (preview && part <= 0 || result.TryGetProperty("trusted", out var trusted) && trusted.ValueKind == JsonValueKind.False)
                    continue;
                var original = (result.GetProperty("text").GetString() ?? "").Trim();
                if (original.Length == 0) continue;
                if (original.Length > 400) original = original[..400];
                var language = result.GetProperty("language").GetString();
                var utterance = root.TryGetProperty("utterance", out var utteranceValue) && utteranceValue.TryGetInt32(out var utteranceNumber) ? utteranceNumber : 0;
                var evidence = new VoiceTrace(Utterance: utterance, Language: language,
                    AudioMs: root.TryGetProperty("audioMs", out var audioValue) && audioValue.TryGetInt32(out var audioMs) ? audioMs : null,
                    CpuMs: root.TryGetProperty("cpuMs", out var cpuValue) && cpuValue.ValueKind == JsonValueKind.Number && cpuValue.TryGetDouble(out var cpu) ? cpu : null);
                var settings = config.Current;
                var skip = selected.Translation == "none" ? "voice-transcript-only" :
                    string.Equals(language, settings.IncomingTargetLanguage.Split('-')[0], StringComparison.OrdinalIgnoreCase) ? "voice-same-language" : null;
                lock (gate)
                {
                    if (generation != epoch || !TeammateSignal || TurnFor(utterance) is not { } turn) continue;
                    long now = Environment.TickCount64;
                    if (now >= turn.Deadline || part > 0 && (part < turn.Part ||
                        part == turn.Part && turn.Caption?.Final == true)) continue;
                    ReadExecutionBackend(root);
                    bool samePart = part > 0 && part == turn.Part;
                    string? finished = turn.Translation?.IsCompletedSuccessfully == true ? turn.Translation.Result : null;
                    bool sameText = skip == null && turn.TranslatedSource == original && turn.TranslatedLanguage == language;
                    bool canReuse = sameText && (turn.Pending != null || turn.Translation is { } task &&
                        (!task.IsCompletedSuccessfully || finished != null));
                    if (!samePart) turn.DraftTranslationStarted = false;
                    bool startTranslation = skip == null && !canReuse && (!preview || !turn.DraftTranslationStarted);
                    bool replaceTranslation = !canReuse && (!samePart || startTranslation || !preview);
                    if (replaceTranslation) turn.StopTranslation();

                    long assigned;
                    if ((samePart || canReuse) && turn.Caption is { } current)
                    {
                        assigned = current.Id;
                        string? retained = skip == null && MatchesTranslationSource(original, turn.TranslatedSource)
                            ? current.Translation : null;
                        if (canReuse && finished != null) retained = finished;
                        turn.Caption = current with { Text = original, Translation = retained,
                            Revision = current.Revision + 1, Final = !preview };
                    }
                    else
                    {
                        assigned = ++captionId;
                        turn.Caption = new(assigned, original, null, Utterance: utterance, Final: !preview);
                    }
                    turn.Part = part;
                    turn.CaptionAt = now;
                    var stage = preview ? "voice-preview-ok" : recovered ? "voice-retry-ok" : "voice-asr-ok";
                    ReportTest(stage, evidence,
                        result.TryGetProperty("milliseconds", out var duration) && duration.TryGetDouble(out var elapsed) ? elapsed : null,
                        original.Length, visible: ReferenceEquals(turn, currentTurn));
                    if (skip != null && !preview) ReportTest(skip, evidence, visible: ReferenceEquals(turn, currentTurn));
                    if (!startTranslation) continue;

                    if (preview) turn.DraftTranslationStarted = true;
                    turn.TranslatedSource = original;
                    turn.TranslatedLanguage = language;
                    var request = new CaptionTranslation(original, language, settings, selected.Translation,
                        generation, assigned, evidence, ct, recovered);
                    if (ReferenceEquals(turn, previousTurn))
                        turn.Pending = request;
                    else
                    {
                        int incomingSlots = selected.Translation == "fast" ? settings.FastConcurrency :
                            settings.LlmConcurrency - (settings.OutgoingEnabled && settings.ReserveSendSlot && settings.LlmConcurrency > 1 ? 1 : 0);
                        if (incomingSlots == 1 && previousTurn?.Translation is { IsCompleted: false })
                            previousTurn.StopTranslation();
                        StartCaptionTranslation(turn, request);
                    }
                }
            }
        }
        finally
        {
            lock (gate)
                if (generation == epoch)
                {
                    currentTurn?.StopTranslation();
                    previousTurn?.StopTranslation();
                }
        }
    }

    private CaptionTurn? TurnFor(int id) => currentTurn?.Utterance == id ? currentTurn :
        previousTurn?.Utterance == id ? previousTurn : null;

    private void StartCaptionTranslation(CaptionTurn turn, CaptionTranslation request)
    {
        turn.Pending = null;
        long remaining = turn.Deadline - Environment.TickCount64;
        if (remaining <= 0 || request.WorkerToken.IsCancellationRequested) return;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(request.WorkerToken);
        var serviceLimit = (request.Mode == "fast" ? 4 : Math.Min(15, request.Settings.LlmTimeoutSeconds)) * 1000L;
        stop.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(serviceLimit, remaining)));
        turn.TranslationStop = stop;
        turn.Translation = TranslateCaption(request.Original, request.Settings, request.Mode, request.Generation,
            turn, request.CaptionId, request.Evidence, stop.Token);
    }

    private static async Task RetireTranslation(CancellationTokenSource stop, Task<string?>? translation)
    {
        using (stop)
        {
            await stop.CancelAsync();
            if (translation != null) await translation;
        }
    }

    private async Task<string?> TranslateCaption(string original, Settings settings, string mode, long generation, CaptionTurn turn, long assigned, VoiceTrace evidence, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        lock (gate) if (generation == epoch) ReportTest("voice-translate-start", evidence, provider: mode);
        try
        {
            // Keep realtime speech translation on the same low-latency reasoning policy as VoiceLab.
            var realtimeSettings = settings with { LlmThinkingEnabled = false, IncomingThinking = false, LlmStreaming = true };
            string translated = mode == "fast"
                ? await translators.Fast(original, settings, ct, true)
                : await translators.Quality(original, [], realtimeSettings, ct, "voice",
                    partial => UpdateCaption(generation, turn, assigned, original, partial, ct));
            var normalized = NormalizeCaptionTranslation(translated);
            UpdateCaption(generation, turn, assigned, original, normalized, ct);
            lock (gate) if (generation == epoch)
                ReportTest(normalized is not null ? "voice-translate-ok" : "voice-translate-empty", evidence,
                    timer.Elapsed.TotalMilliseconds, normalized?.Length, mode,
                    visible: ReferenceEquals(turn, currentTurn) && turn.Caption?.Id == assigned && !ct.IsCancellationRequested);
            return normalized;
        }
        catch (Exception ex)
        {
            // A failed or incomplete stream falls back to the local transcript.
            // The match and utterance checks also guard cancellation on replacement.
            UpdateCaption(generation, turn, assigned, original, null, ct);
            lock (gate) if (generation == epoch)
                ReportTest(ct.IsCancellationRequested ? "voice-translate-canceled" : "voice-translate-error", evidence,
                    timer.Elapsed.TotalMilliseconds, provider: mode, failure: ex.GetType().Name,
                    reason: ct.IsCancellationRequested && turn.Caption?.Id == assigned ? LogReason.Timeout :
                        (ex as ProviderRequestException)?.Reason ?? (ex is HttpRequestException ? LogReason.Network : LogReason.Other),
                    visible: ReferenceEquals(turn, currentTurn) && turn.Caption?.Id == assigned &&
                        MatchesTranslationSource(turn.Caption.Text, original));
            return null;
        }
    }

    private static bool MatchesTranslationSource(string text, string? source)
    {
        if (source is null) return false;
        if (text == source) return true;
        var prefix = source.TrimEnd(' ', '.', '!', '?', ',', '。', '！', '？', '，', '…');
        return prefix.Length > 0 && text.Length > prefix.Length && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            (!char.IsAsciiLetterOrDigit(prefix[^1]) || !char.IsAsciiLetterOrDigit(text[prefix.Length]));
    }

    private static string? NormalizeCaptionTranslation(string? translation)
    {
        var text = translation?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > 500 ? text[..500] : text;
    }

    private void UpdateCaption(long generation, CaptionTurn turn, long assigned, string original, string? translation, CancellationToken ct)
    {
        translation = NormalizeCaptionTranslation(translation);
        lock (gate)
        {
            long now = Environment.TickCount64;
            if (ct.IsCancellationRequested || generation != epoch || !TeammateSignal ||
                !ReferenceEquals(TurnFor(turn.Utterance), turn) || now >= turn.Deadline ||
                turn.Caption is not { } caption || caption.Id != assigned ||
                caption.Translation == translation || !MatchesTranslationSource(caption.Text, original))
                return;
            turn.Caption = caption with { Translation = translation, Revision = caption.Revision + 1 };
            turn.CaptionAt = now;
        }
    }

    private int FindGame()
    {
        if (string.IsNullOrEmpty(gameExecutable)) throw new IOException("无法确认 Deadlock 的安装路径。");
        var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(gameExecutable));
        try
        {
            var ids = candidates.Where(process => string.Equals(GameProcess.ImagePath(process.Id), gameExecutable, StringComparison.OrdinalIgnoreCase))
                .Select(process => process.Id).ToArray();
            if (ids.Length == 1) return ids[0];
        }
        finally { foreach (var process in candidates) process.Dispose(); }
        throw new IOException("没有找到唯一的 Deadlock 游戏进程。");
    }

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        if (supervisor != null) await supervisor;
        await Cuda.DisposeAsync();
        Models.Dispose();
        speechChanged.Dispose();
        shutdown.Dispose();
    }

    private sealed class VoiceEngineStartupException(string message) : IOException(message);
}
