using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace DeadlockTranslate;
public record Incoming(string Id, string Text, string Sender = "", string Channel = "");
public record JobView(string Id,string Text,string Stage,int Revision,bool Done,string? Error,double ElapsedMs);
public sealed class JobState(Incoming input, long created) {
    public Incoming Input { get; } = input;
    public long Created { get; } = created;
    public JobView View { get; set; } = new(input.Id,"","pending",0,false,null,0);
    public int Remaining { get; set; }
    public string[] Context { get; set; } = [];
    public Settings Config { get; set; } = new();
    public CancellationToken SessionToken { get; set; }
}
public sealed class JobEngine : IAsyncDisposable {
    private readonly ConfigStore config; private readonly ITranslator translator;
    private readonly object gate = new();
    private readonly Dictionary<string, JobState> jobs = new();
    // Context is kept per chat channel: an enemy all-chat line must never become the context of a
    // team instruction, and a message with no known channel uses only itself.
    private readonly Dictionary<string, Queue<(long At,string Text)>> history = new();
    private readonly Channel<JobState> fast = Channel.CreateBounded<JobState>(64);
    private readonly Channel<JobState> quality = Channel.CreateBounded<JobState>(64);
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource session = new();
    private readonly Task[] workers;
    private string sessionId = "";
    private long lastClient;
    private long completed,failed;
    private readonly AsyncConcurrency fastSlots,qualitySlots;
    private long cacheEpoch;
    private readonly ConcurrentDictionary<string,(long Until,string Value)> fastCache = new();
    public JobEngine(ConfigStore config, ITranslator translator) {
        this.config=config; this.translator=translator;
        fastSlots=new(config.Current.FastConcurrency);qualitySlots=new(config.Current.LlmConcurrency);
        config.Changed+=UpdateLimits;
        workers = Enumerable.Range(0,8).Select(_=>Worker(fast.Reader,false)).Concat(Enumerable.Range(0,8).Select(_=>Worker(quality.Reader,true))).ToArray();
    }
    private void UpdateLimits(){fastSlots.SetLimit(config.Current.FastConcurrency);qualitySlots.SetLimit(config.Current.LlmConcurrency);}
    public object Stats { get { lock(gate) return new { session = sessionId, messages = jobs.Count, pending=jobs.Values.Count(j=>!j.View.Done), completed, failed, connected = Environment.TickCount64-lastClient<15000 }; } }
    public void ClearCache() { Interlocked.Increment(ref cacheEpoch); fastCache.Clear(); }
    public JobView[] Exchange(string currentSession, Incoming[] inputs, string[] ids) {
        if(currentSession.Length is < 1 or > 100 || inputs.Length>16 || ids.Length>64 || inputs.Any(i=>i.Id.Length is <1 or >100 || i.Text.Length is <1 or >1024 || i.Sender.Length>100 || i.Channel.Length>16)) throw new ArgumentException("消息批次无效");
        lock(gate) {
            lastClient = Environment.TickCount64;
            if(currentSession != sessionId) {
                session.Cancel(); session.Dispose(); session = new(); sessionId = currentSession;
                while(fast.Reader.TryRead(out _)){}
                while(quality.Reader.TryRead(out _)){}
                jobs.Clear(); history.Clear();
            }
            foreach(var input in inputs) {
                if(jobs.ContainsKey(input.Id)) continue;
                var now = Environment.TickCount64;
                var channel = input.Channel is "team" or "all" or "party" ? input.Channel : "";
                var context = Array.Empty<string>();
                if(channel.Length>0) {
                    if(!history.TryGetValue(channel,out var queue)) { queue=new Queue<(long,string)>(); history[channel]=queue; }
                    while(queue.TryPeek(out var oldest) && now-oldest.At>60000) queue.Dequeue();
                    context = queue.TakeLast(6).Select(x=>x.Text).ToArray();
                }
                var job = new JobState(input, now) {Config=config.Current, SessionToken=session.Token, Context=context};
                jobs.Add(input.Id,job);
                if(channel.Length>0) {
                    var queue=history[channel];
                    queue.Enqueue((now,input.Text)); while(queue.Count>6) queue.Dequeue();
                }
                if(!job.Config.Enabled) { job.View=job.View with{Done=true,Stage="skipped"}; continue; }
                var wantFast = job.Config.Mode != "quality"; var wantQuality = job.Config.Mode != "fast";
                job.Remaining=(wantFast?1:0)+(wantQuality?1:0);
                if(wantFast && !fast.Writer.TryWrite(job)) Complete(job,false,null,"快译队列已满");
                if(wantQuality && !quality.Writer.TryWrite(job)) Complete(job,true,null,"精翻队列已满");
            }
            foreach(var old in jobs.Where(x=>x.Value.View.Done && Environment.TickCount64-x.Value.Created>120000).Select(x=>x.Key).ToArray()) jobs.Remove(old);
            while(jobs.Count>500) { var old=jobs.FirstOrDefault(x=>x.Value.View.Done); if(old.Key==null) break; jobs.Remove(old.Key); }
            return ids.Concat(inputs.Select(i=>i.Id)).Distinct().Where(jobs.ContainsKey).Select(id=>jobs[id].View).ToArray();
        }
    }
    private async Task Worker(ChannelReader<JobState> reader, bool isQuality) {
        try {
            await foreach(var job in reader.ReadAllAsync(shutdown.Token)) {
                if(job.SessionToken.IsCancellationRequested) continue;
                using var ct = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token,job.SessionToken);
                string? result=null,error=null;
                try {
                    // Queue waiting has its own deadline; one slow request never occupies other workers.
                    var queueRemaining=35000-(Environment.TickCount64-job.Created);
                    if(queueRemaining<=0)throw new InvalidOperationException("消息积压过久，已跳过");
                    ct.CancelAfter(TimeSpan.FromMilliseconds(queueRemaining));
                    using var slot=await (isQuality?qualitySlots:fastSlots).Enter(ct.Token);
                    ct.Token.ThrowIfCancellationRequested();
                    ct.CancelAfter(TimeSpan.FromSeconds(isQuality?job.Config.LlmTimeoutSeconds:job.Config.FastTimeoutSeconds));
                    var cacheKey = Interlocked.Read(ref cacheEpoch)+"|"+job.Config.FastProvider+"|"+job.Config.BilingualNames+"|"+job.Input.Text;
                    if(!isQuality && fastCache.TryGetValue(cacheKey,out var hit) && hit.Until>Environment.TickCount64) result=hit.Value;
                    else {
                        result=isQuality ? await translator.Quality(job.Input.Text,job.Context,job.Config,ct.Token) : await translator.Fast(job.Input.Text,job.Config,ct.Token);
                        ct.Token.ThrowIfCancellationRequested();
                        if(string.IsNullOrWhiteSpace(result) || result.Length>2500) throw new InvalidDataException("服务返回空内容或过长译文");
                        if(!isQuality) { if(fastCache.Count>=500) fastCache.Clear(); fastCache[cacheKey]=(Environment.TickCount64+300000,result); }
                    }
                } catch(OperationCanceledException) { error="翻译超时或会话已结束"; }
                catch(HttpRequestException ex) { error=ex.StatusCode.HasValue?"翻译服务 HTTP "+(int)ex.StatusCode:"网络连接失败"; }
                catch(InvalidOperationException ex) { error=ex.Message; }
                catch { error="翻译返回格式不兼容或术语校验失败"; }
                if(!job.SessionToken.IsCancellationRequested) Complete(job,isQuality,result,error);
            }
        } catch(OperationCanceledException) { }
    }
    private void Complete(JobState job,bool isQuality,string? text,string? error) {
        lock(gate) {
            var v=job.View; job.Remaining=Math.Max(0,job.Remaining-1);
            if(text != null && (isQuality || v.Stage != "quality")) v=v with{Text=text,Stage=isQuality?"quality":"fast",Revision=v.Revision+1,Error=null};
            if(error != null && v.Stage != "quality") v=v with{Error=error};
            job.View=v with{Done=job.Remaining==0,ElapsedMs=Environment.TickCount64-job.Created};
            if(job.Remaining==0) { completed++; if(job.View.Text.Length==0) failed++; }
        }
    }
    public async ValueTask DisposeAsync() { config.Changed-=UpdateLimits;shutdown.Cancel(); session.Cancel(); await Task.WhenAll(workers); shutdown.Dispose(); session.Dispose(); }
}
