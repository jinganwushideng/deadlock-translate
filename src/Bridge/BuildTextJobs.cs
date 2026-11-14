using System.Text.Json;

namespace DeadlockTranslate;

internal sealed record BuildJobPlan(string Name, string[] Texts, BuildVisibleGroup[]? Groups, string Fingerprint, bool VisibleOnly);
internal sealed record BuildJobField(string Source, string Value);

// Owned by the backend. The shop can disappear while the worker is still running.
internal sealed class BuildTextJobs(CancellationToken stopping) {
    private readonly object gate = new();
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);

    internal bool Start(string key, string revision, Func<Job, Task> work) {
        lock (gate) {
            if (jobs.TryGetValue(key, out var existing)) return existing.Revision == revision;
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            foreach (var entry in jobs.Where(entry => entry.Value.Ended && entry.Value.LastSeen < cutoff).ToArray())
                jobs.Remove(entry.Key);
            if (jobs.Values.Count(job => !job.Ended) >= 4) return false;
            if (jobs.Count >= 24) {
                var oldest = jobs.Where(entry => entry.Value.Ended).MinBy(entry => entry.Value.LastSeen);
                jobs.Remove(oldest.Key);
            }
            var job = new Job(revision, stopping);
            jobs.Add(key, job);
            _ = Task.Run(async () => {
                using var lifetime = job.Lifetime;
                try { await work(job); }
                catch (OperationCanceledException) when (job.WorkToken.IsCancellationRequested) { job.Fail("cancelled"); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { job.Fail("unavailable"); }
                finally { job.Finish(); }
            });
            return true;
        }
    }

    internal Job? Find(string key, string revision) {
        lock (gate) return jobs.TryGetValue(key, out var job) && job.Revision == revision ? job : null;
    }

    internal sealed class Job(string revision, CancellationToken stopping) {
        private readonly object gate = new();
        private readonly List<BuildJobField> updates = [];
        private bool ended, working;
        private int total, done, cached, translated, failed;
        private string? error;
        private DateTime lastSeen = DateTime.UtcNow;
        private BuildJobPlan? plan;
        internal CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        internal CancellationToken WorkToken => Lifetime.Token;
        internal string Revision { get; } = revision;
        internal bool Ended { get { lock (gate) return ended; } }
        internal bool HasError { get { lock (gate) return error != null; } }
        internal BuildJobPlan? Plan { get { lock (gate) return plan; } }
        internal DateTime LastSeen { get { lock (gate) return lastSeen; } }

        internal void SetPlan(BuildJobPlan value) { lock (gate) { plan = value; total = value.Texts.Length; } }
        internal void BeginTranslation() { lock (gate) working = true; }
        internal void Fail(string code) { lock (gate) error = code; }
        internal void FailedFields(int count) { lock (gate) failed += count; }
        internal void Finish() { lock (gate) { ended = true; working = false; lastSeen = DateTime.UtcNow; } }
        internal void Cancel() { try { Lifetime.Cancel(); } catch (ObjectDisposedException) {} }
        internal void Apply(string[] sources, JsonElement reply, bool refinement) {
            lock (gate) {
                if (reply.TryGetProperty("cacheHits", out var hits)) cached += hits.GetInt32();
                if (reply.TryGetProperty("translated", out var added)) translated += added.GetInt32();
                var translations = reply.GetProperty("translations");
                for (var i = 0; i < sources.Length; i++) {
                    if (translations[i].GetString() is not string value) continue;
                    updates.Add(new(sources[i], value));
                    if (!refinement) done++;
                }
            }
        }
        internal object Snapshot(int cursor, int id) {
            lock (gate) {
                lastSeen = DateTime.UtcNow;
                cursor = Math.Clamp(cursor, 0, updates.Count);
                var fields = updates.Skip(cursor).Take(12).ToArray();
                return new { id, revision = Revision, cursor = cursor + fields.Length,
                    ended, more = cursor + fields.Length < updates.Count, working, total, done,
                    cacheHits = cached, translated, failed, error,
                    plan = cursor == 0 && plan != null ? new { plan.Name, plan.Groups, plan.Fingerprint, plan.VisibleOnly } : null,
                    fields };
            }
        }
    }

    internal static IEnumerable<string[]> Batches(IEnumerable<string> texts, int limit, int charLimit) {
        var batch = new List<string>();
        var size = 0;
        foreach (var text in texts) {
            if (batch.Count > 0 && (batch.Count >= limit || size + text.Length > charLimit)) {
                yield return batch.ToArray();
                batch.Clear();
                size = 0;
            }
            batch.Add(text);
            size += text.Length;
        }
        if (batch.Count > 0) yield return batch.ToArray();
    }
}
