using System.Text.Json;

namespace DeadlockTranslate;

internal sealed record BuildJobPlan(string Name, string[] Texts, BuildVisibleGroup[]? Groups, string Fingerprint, bool VisibleOnly,BuildSourceField[]? Fields=null,bool FullSource=false);
internal sealed record BuildJobField(string Source, string Value,string? FieldId=null,string? Kind=null,uint ItemId=0);

// Owned by the backend. The shop can disappear while the worker is still running.
internal sealed class BuildTextJobs(CancellationToken stopping) {
    internal const int MaxParallelBatches=2;
    private readonly object gate = new();
    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Job> canonical = new(StringComparer.Ordinal);

    internal bool Start(string key, string revision, Func<Job, Task> work,string? identity=null,int priority=2) {
        lock (gate) {
            if (jobs.TryGetValue(key, out var existing)) return existing.Revision == revision;
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            foreach (var entry in jobs.Where(entry => entry.Value.Ended && entry.Value.LastSeen < cutoff).ToArray())
                jobs.Remove(entry.Key);
            foreach(var entry in canonical.Where(entry=>entry.Value.Ended&&entry.Value.LastSeen<cutoff).ToArray())canonical.Remove(entry.Key);
            if (jobs.Count >= 24) {
                var oldest = jobs.Where(entry => entry.Value.Ended).MinBy(entry => entry.Value.LastSeen);
                if(oldest.Key==null)return false;
                jobs.Remove(oldest.Key);
            }
            if(identity!=null&&canonical.TryGetValue(identity,out var shared)&&shared.Revision==revision&&!shared.HasError&&!shared.HasFailures){
                jobs[key]=shared;
                return true;
            }
            if (jobs.Values.Distinct().Count(job => !job.Ended) >= 4) return false;
            var job = new Job(revision, stopping,priority);
            jobs.Add(key, job);
            if(identity!=null)canonical[identity]=job;
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
    internal async Task WaitTurn(Job current,CancellationToken token) {
        while(true){
            token.ThrowIfCancellationRequested();
            lock(gate){
                var now=DateTime.UtcNow;
                var next=jobs.Values.Distinct().Where(job=>!job.Ended&&!job.HasError&&job.Working)
                    .OrderByDescending(job=>(now-job.LastSeen).TotalSeconds<2?job.Priority:0)
                    .ThenByDescending(job=>job.LastSeen).FirstOrDefault();
                if(next==null||ReferenceEquals(next,current))return;
            }
            await Task.Delay(100,token);
        }
    }

    internal sealed class Job(string revision, CancellationToken stopping,int priority) {
        private readonly object gate = new();
        private readonly List<BuildJobField> updates = [];
        private bool ended, working;
        private int total, done, cached, local, translated, failed;
        private string? error;
        private DateTime lastSeen = DateTime.UtcNow;
        private BuildJobPlan? plan;
        internal CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        internal CancellationToken WorkToken => Lifetime.Token;
        internal string Revision { get; } = revision;
        internal bool Ended { get { lock (gate) return ended; } }
        internal bool HasError { get { lock (gate) return error != null; } }
        internal bool HasFailures { get { lock(gate)return failed>0; } }
        internal bool Working { get { lock(gate)return working; } }
        internal int Priority { get; }=priority;
        internal BuildJobPlan? Plan { get { lock (gate) return plan; } }
        internal DateTime LastSeen { get { lock (gate) return lastSeen; } }

        internal void SetPlan(BuildJobPlan value) { lock (gate) { plan = value; total = value.Texts.Length; } }
        internal void BeginTranslation() { lock (gate) working = true; }
        internal void Fail(string code) { lock (gate) error ??= code; }
        internal void FailedFields(int count) { lock (gate) failed += count; }
        internal void Finish() { lock (gate) { ended = true; working = false; lastSeen = DateTime.UtcNow; } }
        internal void Cancel() { try { Lifetime.Cancel(); } catch (ObjectDisposedException) {} }
        internal void Apply(string[] sources, JsonElement reply, bool refinement) {
            lock (gate) {
                if (reply.TryGetProperty("cacheHits", out var hits)) cached += hits.GetInt32();
                if (reply.TryGetProperty("localHits", out var localHits)) local += localHits.GetInt32();
                if (reply.TryGetProperty("translated", out var added)) translated += added.GetInt32();
                var translations = reply.GetProperty("translations");
                for (var i = 0; i < sources.Length; i++) {
                    if (translations[i].GetString() is not string value) continue;
                    var fields=plan?.Fields?.Where(field=>field.Text==sources[i]).ToArray();
                    if(fields is {Length:>0})
                        foreach(var field in fields)updates.Add(new(sources[i],value,field.Id,field.Kind,field.ItemId));
                    else updates.Add(new(sources[i], value));
                    if (!refinement) done++;
                }
            }
        }
        internal object Snapshot(int cursor, int id) {
            lock (gate) {
                lastSeen = DateTime.UtcNow;
                cursor = Math.Clamp(cursor, 0, updates.Count);
                var page=new List<BuildJobField>();
                var characters=0;
                foreach(var field in updates.Skip(cursor).Take(12)){
                    var size=field.Source.Length+field.Value.Length;
                    if(page.Count>0&&characters+size>24000)break;
                    page.Add(field);characters+=size;
                }
                var fields=page.ToArray();
                return new { id, revision = Revision, cursor = cursor + fields.Length, fieldCount=updates.Count,
                    ended, more = cursor + fields.Length < updates.Count, working, total, done,
                    cacheHits = cached, localHits = local, translated, failed, error,
                    plan = cursor == 0 && plan != null ? new { plan.Name, Groups=plan.FullSource?null:plan.Groups, plan.Fingerprint, plan.VisibleOnly } : null,
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
    internal static async Task RunBatches(IEnumerable<string[]> batches,Func<string[],Task> translate,Func<bool> stopped,CancellationToken token){
        var queue=new System.Collections.Concurrent.ConcurrentQueue<string[]>(batches);
        async Task Worker(){
            while(!stopped()&&queue.TryDequeue(out var batch)){
                token.ThrowIfCancellationRequested();
                await translate(batch);
            }
        }
        await Task.WhenAll(Enumerable.Range(0,MaxParallelBatches).Select(_=>Worker()));
    }
    internal static string[] PrioritizeSource(LocalHeroBuild source,IEnumerable<string> visible) {
        var shown=visible.ToHashSet(StringComparer.Ordinal);
        var names=source.Groups.Select(group=>group.Name).ToHashSet(StringComparer.Ordinal);
        var notes=source.Groups.Select(group=>group.Note).Append(source.Description).ToHashSet(StringComparer.Ordinal);
        // Native protobuf writes equipment entries before a group's name/note.
        // Prioritize the page headings instead of inheriting that serialization order.
        int Rank(string text)=>text==source.Name?0:names.Contains(text)?1:notes.Contains(text)?shown.Contains(text)?2:3:4;
        return source.Texts.OrderBy(Rank).ToArray();
    }
    internal static bool IsChineseTitle(string title) {
        title=System.Text.RegularExpressions.Regex.Replace(title,@"<[^>]*>|\[[^\]]*(?:\d\s*%|https?:|\.[a-z]{2,})[^\]]*\]","",System.Text.RegularExpressions.RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(100));
        var han=title.Count(c=>c is >= '\u3400' and <= '\u9fff');var latin=title.Count(char.IsAsciiLetter);
        return han>=2&&han>=latin*0.5;
    }
    internal static bool IsChinesePlan(LocalHeroBuild source) {
        if(IsChineseTitle(source.Name))return true;
        var text=string.Join('\n',source.Texts);var han=text.Count(c=>c is >= '\u3400' and <= '\u9fff');var latin=text.Count(char.IsAsciiLetter);
        return han>=8&&han>=latin*0.5;
    }
}
