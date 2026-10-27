using System.Diagnostics;

namespace DeadlockTranslate;

public sealed record GamePrivilegeStatus(bool? Detected, bool? Elevated, bool? SameUser);

// Read only when settings/diagnostics request it, at most once per three seconds.
// This is outside the keyboard callback and does not depend on foreground focus.
public sealed class GamePrivilegeDiagnostics
{
    readonly object gate = new();
    readonly Func<GamePrivilegeStatus> sample;
    readonly Func<long> clock;
    GamePrivilegeStatus cached = new(null, null, null);
    long checkedAt;
    bool checkedOnce;

    public GamePrivilegeDiagnostics(Func<GamePrivilegeStatus>? sample = null, Func<long>? clock = null, string? expectedExecutable = null)
    {
        this.sample = sample ?? (() => Sample(expectedExecutable));
        this.clock = clock ?? (() => Environment.TickCount64);
    }
    public GamePrivilegeStatus Inspect(bool force = false)
    {
        lock (gate)
        {
            var now = clock();
            if (force || !checkedOnce || now - checkedAt >= 3000)
            {
                try { cached = sample(); }
                catch { cached = new(null, null, null); }
                checkedAt = now; checkedOnce = true;
            }
            return cached;
        }
    }
    static GamePrivilegeStatus Sample(string? expectedExecutable)
    {
        var candidates = new List<Process>();
        try
        {
            candidates.AddRange(Process.GetProcessesByName("deadlock"));
            candidates.AddRange(Process.GetProcessesByName("citadel"));
            if (candidates.Count == 0) return new(false, null, null);
            var selected = new List<Process>(); var unknownPath = false;
            foreach (var process in candidates)
            {
                if (string.IsNullOrWhiteSpace(expectedExecutable)) { selected.Add(process); continue; }
                var path = GameProcess.ImagePath(process.Id);
                if (path == null) { unknownPath = true; continue; }
                if (string.Equals(path, expectedExecutable, StringComparison.OrdinalIgnoreCase)) selected.Add(process);
            }
            if (selected.Count == 0) return new(unknownPath ? null : false, null, null);
            var elevation = selected.Select(p => StartupCompatibility.ProcessElevated(p.Id)).ToArray();
            var users = selected.Select(p => StartupCompatibility.SameUser(p.Id)).ToArray();
            return new(true,
                elevation.Any(v => v == true) ? true : elevation.All(v => v == false) ? false : null,
                users.Any(v => v == false) ? false : users.All(v => v == true) ? true : null);
        }
        finally { foreach (var process in candidates) process.Dispose(); }
    }
}
