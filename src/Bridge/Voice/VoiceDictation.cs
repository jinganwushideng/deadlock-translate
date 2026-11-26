namespace DeadlockTranslate;

internal sealed record DictationSignal(string Id, string Action, string Device, string Language);
public sealed record DictatedMessage(OutgoingInput Input, Settings Settings, bool Translate);

internal sealed class VoiceDictation(Func<OutgoingLanguageSelection?> sendLanguage, Func<long>? time = null)
{
    private readonly object gate = new();
    private readonly Func<long> clock = time ?? (() => Environment.TickCount64);
    private string owner = "";
    private long seenAt;
    private string? id, opener, channel;
    private string phase = "idle", draft = "";
    private string? previewText;
    private long recordingAt, finishAt, noticeUntil;
    private string? error;
    private bool capped, translate, ready;
    private Settings? sendSettings;
    private DictatedMessage? completed;
    private (string Action, HotkeyChord Chord)[] bindings = [];
    private Settings current = new();
    private readonly bool[] held = new bool[256];

    private static (string Action, string Label, string Key)[] Keys(Settings s) =>
        [("team", "队伍语音键", s.VoiceInputTeamKey), ("all", "全体语音键", s.VoiceInputAllKey),
            ("translate", "翻译发送键", s.VoiceInputTranslateKey), ("switch", "切换频道键", s.VoiceInputChannelKey)];

    internal static bool CanUse(Settings s) => s.VoiceInputEnabled && s.OutgoingEnabled &&
        (!string.IsNullOrWhiteSpace(s.VoiceInputTeamKey) || !string.IsNullOrWhiteSpace(s.VoiceInputAllKey));

    public static void Validate(Settings s)
    {
        if (s.VoiceInputDevice is null || s.VoiceInputDevice.Length > 256 || s.VoiceInputDevice.Any(char.IsControl) ||
            s.VoiceInputLanguage is not ("auto" or "zh" or "en" or "ja" or "ko" or "ru" or "es" or "pt" or "fr" or "de"))
            throw new ArgumentException("请选择有效的麦克风与口述语言。");
        var used = s.QuickMessages.Where(q => q.Enabled).Select(q => HotkeyChord.Parse(q.Hotkey))
            .Concat(s.OutgoingLanguageBindings.Where(q => q.Enabled).Select(q => HotkeyChord.Parse(q.Hotkey)))
            .Append(HotkeyChord.Parse(s.OutgoingHotkey)).ToList();
        foreach (var item in Keys(s))
        {
            if (string.IsNullOrWhiteSpace(item.Key)) continue;
            if (!HotkeyChord.TryParse(item.Key, out var chord))
                throw new ArgumentException($"「{item.Label}」格式无效。请录制修饰键加字母、数字、空格、Enter、Tab，或单独使用 F1–F12。");
            if (used.Any(existing => QuickMessages.Overlap(existing, chord!)))
                throw new ArgumentException($"「{item.Label}」与现有快捷键冲突，请更换组合。");
            used.Add(chord!);
        }
    }

    public void Configure(Settings settings)
    {
        var next = Keys(settings).Where(k => !string.IsNullOrWhiteSpace(k.Key)).Select(k => (k.Action, HotkeyChord.Parse(k.Key))).ToArray();
        lock (gate)
        {
            if (current.VoiceInputEnabled != settings.VoiceInputEnabled || current.VoiceInputDevice != settings.VoiceInputDevice ||
                current.VoiceInputLanguage != settings.VoiceInputLanguage || !bindings.SequenceEqual(next) || !settings.OutgoingEnabled)
                Clear();
            current = settings;
            bindings = next;
        }
    }

    public void Observe(string session, bool inMatch, bool foreground)
    {
        lock (gate)
        {
            if (owner != session || !inMatch || !foreground) Clear();
            owner = session;
            seenAt = inMatch && foreground ? clock() : 0;
        }
    }

    private bool Live => CanUse(current) && seenAt > 0 && clock() - seenAt < 1250;
    public bool CanSend { get { lock (gate) return Live; } }

    public bool Process(int key, int flags, bool down, bool up, KeyModifiers modifiers, bool allowed)
    {
        if (key is < 0 or > 255 || (flags & 0x10) != 0) return false;
        lock (gate)
        {
            if (held[key])
            {
                if (up) held[key] = false;
                return true;
            }
            if (!down || !allowed || !Live) return false;
            if (key == 0x1b && modifiers == 0 && id is not null)
            {
                Clear();
                held[key] = true;
                return true;
            }
            var action = Array.Find(bindings, item => item.Chord.VirtualKey == key && item.Chord.Matches(modifiers)).Action;
            if (action is null || id is null && action is not ("team" or "all")) return false;
            held[key] = true;
            if (id is null)
            {
                if (!ready || completed is not null) { Notice("语音引擎尚未就绪，请稍后再试。"); return true; }
                error = null;
                id = "voice-" + Guid.NewGuid().ToString("N");
                opener = channel = action;
                phase = "starting";
                recordingAt = clock();
                return true;
            }
            if (sendSettings is not null) return true;
            if (action == "switch") channel = channel == "team" ? "all" : "team";
            else if (action is "team" or "all" && action != opener) channel = action;
            else
            {
                translate = action == "translate";
                // Capture the already prepared settings and the current language at the key edge.
                var language = sendLanguage();
                sendSettings = language is null ? current : current with { OutgoingTargetLanguage = language.Language,
                    OutgoingCustomLanguage = language.Name, OutgoingCustomLanguageCode = language.Code };
                if (phase == "review") Complete();
                else Finish();
            }
            return true;
        }
    }

    public void RecoverReleases(Func<int,bool> down)
    {
        lock (gate)
            for (var key = 0; key < held.Length; key++)
                if (held[key] && !down(key)) held[key] = false;
    }

    public DictationSignal? Poll(bool workerReady, bool foreground)
    {
        lock (gate)
        {
            ready = workerReady;
            if (!Live || !foreground) { Clear(); return null; }
            if (id is null || phase == "review") return null;
            var now = clock();
            if (phase == "recording" && now - recordingAt >= 30000) { capped = true; Finish(); }
            if (phase == "starting" && now - recordingAt >= 4000)
            {
                Clear(); Notice("麦克风或语音识别超时，请重试。"); return null;
            }
            if (phase == "finishing" && now - finishAt >= 90000)
            {
                previewText = null;
                if (string.IsNullOrWhiteSpace(draft)) Clear();
                else { phase = "review"; sendSettings = null; }
                Notice("识别处理超时，请确认草稿后再发送。");
                return null;
            }
            return new(id, phase == "finishing" ? "finish" : "record", current.VoiceInputDevice, current.VoiceInputLanguage);
        }
    }

    public void Receive(string token, string state, string? text)
    {
        lock (gate)
        {
            if (!Live || token != id || phase == "review") return;
            if (state == "error") { Clear(); Notice("麦克风不可用，请检查设备或 Windows 麦克风权限。"); return; }
            if (state == "recording" && phase == "starting") { phase = state; recordingAt = clock(); }
            if (state == "preview" && phase == "recording") previewText = text;
            if (state == "partial") { draft = text ?? ""; previewText = null; }
            if (state != "final" || phase != "finishing") return;
            draft = text ?? "";
            previewText = null;
            if (sendSettings is not null) Complete();
            else if (capped && !string.IsNullOrWhiteSpace(draft)) phase = "review";
            else Clear();
        }
    }

    public DictatedMessage? Take(string session)
    {
        lock (gate)
        {
            if (!Live || session != owner) return null;
            var value = completed;
            completed = null;
            return value;
        }
    }

    public object? View(string session)
    {
        lock (gate)
        {
            if (!CanUse(current) || owner != session) return null;
            var visible = previewText ?? draft;
            return new { enabled = true, canSend = Live, id, state = phase,
                text = visible.Length > 300 ? "…" + string.Concat(visible.EnumerateRunes().TakeLast(300).Select(rune => rune.ToString())) : visible, channel,
                elapsed = phase == "recording" ? (clock() - recordingAt) / 1000 : 0,
                error = clock() < noticeUntil ? error : null,
                originalKey = bindings.FirstOrDefault(item => item.Action == opener).Chord?.Label,
                translateKey = bindings.FirstOrDefault(item => item.Action == "translate").Chord?.Label };
        }
    }

    public void Fail(string message) { lock (gate) { ready = false; if (id is not null) { Clear(); Notice(message); } } }
    public void Cancel() { lock (gate) { ready = false; Clear(); } }

    private void Finish() { phase = "finishing"; finishAt = clock(); }
    private void Notice(string message) { error = message; noticeUntil = clock() + 3500; }

    private void Complete()
    {
        if (!string.IsNullOrWhiteSpace(draft) && sendSettings is { } settings)
            completed = new(new(id!, draft, channel!, translate ? settings.OutgoingDisplayMode : "translation"), settings, translate);
        Clear(keepCompleted: true);
    }

    private void Clear(bool keepCompleted = false)
    {
        id = opener = channel = error = null;
        phase = "idle";
        draft = "";
        previewText = null;
        capped = false;
        sendSettings = null;
        if (!keepCompleted) completed = null;
    }
}
