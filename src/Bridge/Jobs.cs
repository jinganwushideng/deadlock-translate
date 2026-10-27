using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace DeadlockTranslate;
public record Incoming(string Id, string Text, string Sender = "", string Channel = "",bool ContextOnly=false);
public record JobView(string Id,string Text,string Stage,int Revision,bool Done,string? Error,double ElapsedMs);
public record ReceiveScheduling(int Threshold,int Unfinished,bool Burst,int FastLimit,int QualityLimit);
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
    private readonly DiagnosticLog? log;
    private readonly object gate = new();
    private readonly Dictionary<string, JobState> jobs = new();
    private readonly HashSet<string> retiredSessions = new();
    private readonly Queue<string> retiredOrder = new();
    // Context is kept per chat channel: an enemy all-chat line must never become the context of a
    // team instruction, and a message with no known channel uses only itself.
    private readonly ChannelHistory history=new();
    private readonly Channel<JobState> fast = Channel.CreateBounded<JobState>(64);
    private readonly Channel<JobState> quality = Channel.CreateBounded<JobState>(64);
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource session = new();
    private readonly Task[] workers;
    private string sessionId = "";
    private long lastClient;
    private long completed,failed;
    private const int BurstThreshold=2;
    private int unfinishedReceive,fastLimit=1,qualityLimit=1;
    private readonly AsyncConcurrency fastSlots,qualitySlots;
    private long cacheEpoch;
    private readonly ConcurrentDictionary<string,(long Until,string Value)> fastCache = new();
    public JobEngine(ConfigStore config, ITranslator translator,DiagnosticLog? log=null) {
        this.config=config; this.translator=translator;this.log=log;
        fastSlots=new(1);qualitySlots=new(1);
        config.Changed+=UpdateLimits;
        workers = Enumerable.Range(0,8).Select(_=>Worker(fast.Reader,false)).Concat(Enumerable.Range(0,8).Select(_=>Worker(quality.Reader,true))).ToArray();
    }
    private void UpdateLimits(){lock(gate)ApplyReceiveLimits();}
    private void ApplyReceiveLimits(){
        var settings=config.Current;var burst=unfinishedReceive>=BurstThreshold;
        var nextFast=burst?settings.FastConcurrency:1;var nextQuality=burst?settings.LlmConcurrency:1;
        if(nextFast!=fastLimit){fastLimit=nextFast;fastSlots.SetLimit(nextFast);}
        if(nextQuality!=qualityLimit){qualityLimit=nextQuality;qualitySlots.SetLimit(nextQuality);}
    }
    public ReceiveScheduling Scheduling {get{lock(gate)return new(BurstThreshold,unfinishedReceive,unfinishedReceive>=BurstThreshold,fastLimit,qualityLimit);}}
    public bool Connected {get{lock(gate)return lastClient>0&&Environment.TickCount64-lastClient<15000;}}
    public object Stats { get { lock(gate) return new { session = sessionId, messages = jobs.Count, pending=unfinishedReceive, scheduling=Scheduling, completed, failed, connected = Connected }; } }
    public void ClearCache() { Interlocked.Increment(ref cacheEpoch); fastCache.Clear(); }
    public bool IsCurrentSession(string id) { lock(gate) return sessionId == id; }
    public JobView[] Exchange(string currentSession, Incoming[] inputs, string[] ids) {
        if(currentSession.Length is < 1 or > 100 || inputs.Length>16 || ids.Length>64 || inputs.Any(i=>i.Id.Length is <1 or >100 || i.Text.Length is <1 or >1024 || i.Sender.Length>100 || i.Channel.Length>16)) throw new ArgumentException("消息批次无效");
        lock(gate) {
            // A delayed request from a previous chat owner must not roll the entire
            // engine back to that match and cancel the current owner's pending work.
            if(currentSession != sessionId && retiredSessions.Contains(currentSession)) return [];
            lastClient = Environment.TickCount64;
            if(currentSession != sessionId) {
                if(sessionId.Length>0) {retiredSessions.Add(sessionId);retiredOrder.Enqueue(sessionId);while(retiredOrder.Count>64)retiredSessions.Remove(retiredOrder.Dequeue());}
                session.Cancel(); session.Dispose(); session = new(); sessionId = currentSession;
                while(fast.Reader.TryRead(out _)){}
                while(quality.Reader.TryRead(out _)){}
                jobs.Clear(); history.Reset(currentSession);unfinishedReceive=0;ApplyReceiveLimits();
            }
            foreach(var input in inputs) {
                if(jobs.ContainsKey(input.Id)) continue;
                var now = Environment.TickCount64;
                var channel = input.Channel is "team" or "all" or "party" ? input.Channel : "";
                var context = history.Recent(currentSession,channel,6,60);
                var job = new JobState(input, now) {Config=config.Current, SessionToken=session.Token, Context=context};
                jobs.Add(input.Id,job);
                history.Add(currentSession,channel,input.Text);
                if(input.ContextOnly||!job.Config.Enabled) { job.View=job.View with{Done=true,Stage="skipped"}; continue; }
                var wantFast = job.Config.Mode != "quality"; var wantQuality = job.Config.Mode != "fast";
                job.Remaining=(wantFast?1:0)+(wantQuality?1:0);
                unfinishedReceive++;
                if(wantFast && !fast.Writer.TryWrite(job)) Complete(job,false,null,"快译队列已满");
                if(wantQuality && !quality.Writer.TryWrite(job)) Complete(job,true,null,"精翻队列已满");
            }
            foreach(var old in jobs.Where(x=>x.Value.View.Done && Environment.TickCount64-x.Value.Created>120000).Select(x=>x.Key).ToArray()) jobs.Remove(old);
            while(jobs.Count>500) { var old=jobs.FirstOrDefault(x=>x.Value.View.Done); if(old.Key==null) break; jobs.Remove(old.Key); }
            // Count logical messages, not fast/quality stages or a browser batch. A second
            // message arriving later immediately wakes existing admission waiters.
            ApplyReceiveLimits();
            return ids.Concat(inputs.Select(i=>i.Id)).Distinct().Where(jobs.ContainsKey).Select(id=>jobs[id].View).ToArray();
        }
    }
    public string[] OutgoingContext(string session,string channel,Settings settings)=>settings.OutgoingUseContext?history.Recent(session,channel,settings.OutgoingContextCount,settings.OutgoingContextSeconds):[];
    private async Task Worker(ChannelReader<JobState> reader, bool isQuality) {
        try {
            await foreach(var job in reader.ReadAllAsync(shutdown.Token)) {
                if(job.SessionToken.IsCancellationRequested) continue;
                using var ct = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token,job.SessionToken);
                string? result=null,error=null;HttpRequestException? httpFailure=null;string? failureType=null;LogReason? outputReason=null;
                try {
                    // Queue waiting has its own deadline; one slow request never occupies other workers.
                    var queueRemaining=35000-(Environment.TickCount64-job.Created);
                    if(queueRemaining<=0)throw new InvalidOperationException("消息积压过久，已跳过");
                    ct.CancelAfter(TimeSpan.FromMilliseconds(queueRemaining));
                    using var slot=await (isQuality?qualitySlots:fastSlots).Enter(ct.Token);
                    ct.Token.ThrowIfCancellationRequested();
                    ct.CancelAfter(TimeSpan.FromSeconds(isQuality?job.Config.LlmTimeoutSeconds:job.Config.FastTimeoutSeconds));
                    var cacheKey = Interlocked.Read(ref cacheEpoch)+"|"+job.Config.FastProvider+"|"+job.Config.BilingualNames+"|"+job.Config.ChineseNameStyle+"|"+job.Config.IncomingTargetLanguage+"|"+job.Config.IncomingCustomLanguage+"|"+job.Config.IncomingCustomLanguageCode+"|"+job.Input.Text;
                    if(!isQuality && fastCache.TryGetValue(cacheKey,out var hit) && hit.Until>Environment.TickCount64) result=hit.Value;
                    else {
                        var request=isQuality ? translator.Quality(job.Input.Text,job.Context,job.Config,ct.Token) : translator.Fast(job.Input.Text,job.Config,ct.Token);
                        try {result=await request.WaitAsync(ct.Token);}
                        catch(OperationCanceledException) {
                            // Retire the worker lease even if a provider ignores cancellation.
                            // Observe a possible late fault without restoring an old scene/job.
                            _=request.ContinueWith(static task=>{_ = task.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
                            throw;
                        }
                        ct.Token.ThrowIfCancellationRequested();
                        if(string.IsNullOrWhiteSpace(result) || result.Length>2500) throw new InvalidDataException("服务返回空内容或过长译文");
                        if(!isQuality) { if(fastCache.Count>=500) fastCache.Clear(); fastCache[cacheKey]=(Environment.TickCount64+300000,result); }
                    }
                } catch(OperationCanceledException) { error="翻译超时或会话已结束";failureType="OperationCanceledException"; }
                catch(HttpRequestException ex) { httpFailure=ex;failureType=ex.GetType().Name;error=ex is ProviderRequestException or ProviderNetworkException?ex.Message:ex.StatusCode.HasValue?"翻译服务 HTTP "+(int)ex.StatusCode:"网络连接失败"; }
                catch(TranslationOutputException ex) { error=ex.Message;failureType=ex.GetType().Name;outputReason=ex.Reason; }
                catch(InvalidOperationException ex) { error=ex.Message;failureType=ex.GetType().Name; }
                catch(Exception ex) { error="翻译返回格式不兼容或术语校验失败";failureType=ex.GetType().Name; }
                if(!job.SessionToken.IsCancellationRequested) Complete(job,isQuality,result,error,httpFailure,failureType,outputReason);
            }
        } catch(OperationCanceledException) { }
    }
    private void Complete(JobState job,bool isQuality,string? text,string? error,HttpRequestException? httpFailure=null,string? failureType=null,LogReason? outputReason=null) {
        var providerFailure=httpFailure as ProviderRequestException;
        var reason=error==null?LogReason.None:providerFailure?.Reason??outputReason??(error.Contains("超时")?LogReason.Timeout:httpFailure?.StatusCode==System.Net.HttpStatusCode.TooManyRequests?LogReason.RateLimit:httpFailure?.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden?LogReason.Unauthorized:httpFailure!=null?LogReason.Network:LogReason.Other);
        lock(gate) {
            if(job.SessionToken.IsCancellationRequested || !jobs.TryGetValue(job.Input.Id,out var current) || !ReferenceEquals(current,job) || job.Remaining==0) return;
            log?.Record("translation",error==null?LogKind.TranslationCompleted:LogKind.TranslationFailed,new(Reason:reason,Token:DiagnosticLog.Token(sessionId,job.Input.Id),Provider:isQuality?"quality":job.Config.FastProvider,Stage:(httpFailure as ProviderNetworkException)?.Stage??providerFailure?.Stage??(isQuality?"quality":"fast"),HttpStatus:(int?)httpFailure?.StatusCode,FailureType:failureType,ElapsedMs:Environment.TickCount64-job.Created,NetworkError:httpFailure==null?null:ProviderNetworkFailure.Code(httpFailure),SocketError:httpFailure==null?null:(int?)ProviderNetworkFailure.SocketCode(httpFailure)));
            var v=job.View; job.Remaining=Math.Max(0,job.Remaining-1);
            if(text != null && (isQuality || v.Stage != "quality")) v=v with{Text=text,Stage=isQuality?"quality":"fast",Revision=v.Revision+1,Error=null};
            if(error != null && v.Stage != "quality") v=v with{Error=error};
            job.View=v with{Done=job.Remaining==0,ElapsedMs=Environment.TickCount64-job.Created};
            if(job.Remaining==0) { unfinishedReceive--; completed++; if(job.View.Text.Length==0) failed++;ApplyReceiveLimits(); }
        }
    }
    public async ValueTask DisposeAsync() { config.Changed-=UpdateLimits;shutdown.Cancel(); session.Cancel(); await Task.WhenAll(workers); shutdown.Dispose(); session.Dispose(); }
}
