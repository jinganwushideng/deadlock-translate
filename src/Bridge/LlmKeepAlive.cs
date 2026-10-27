namespace DeadlockTranslate;

public sealed record KeepAliveOutcome(string State,LogReason Reason=LogReason.None,int? HttpStatus=null,int RetryAfterSeconds=0,bool Attempted=false,long ElapsedMs=0);
public interface ILlmKeepAliveProvider {
    bool HasKeepAliveCredentials {get;}
    long LastModelActivity(string model);
    Task<KeepAliveOutcome> TryKeepAlive(Settings settings,string model,CancellationToken cancellation);
    void CancelKeepAlive();
}
public sealed record KeepAliveStatus(bool Enabled,int IntervalSeconds,string State,long Requests,long Successes,DateTimeOffset? LastAttemptUtc,long LastElapsedMs,int? HttpStatus,int Models);

// No game-frame hooks, chat content or extra process. At most two distinct saved models.
public sealed class LlmKeepAlive : IAsyncDisposable {
    private readonly ConfigStore config;private readonly ILlmKeepAliveProvider provider;private readonly Func<bool> connected;private readonly Func<long> clock;private readonly DiagnosticLog? log;
    private readonly CancellationTokenSource stop;private readonly SemaphoreSlim runGate=new(1);private readonly object gate=new();private readonly Task loop;
    private readonly Dictionary<string,long> due=new(StringComparer.Ordinal);private readonly Dictionary<string,int> failures=new(StringComparer.Ordinal);
    private string state="disabled";private long requests,successes,lastAttempt=-60000,lastElapsed,revision;private DateTimeOffset? lastAt;private int? lastHttp;private int models;
    public LlmKeepAlive(ConfigStore config,ILlmKeepAliveProvider provider,Func<bool> connected,DiagnosticLog? log=null,CancellationToken lifetime=default,bool start=true,Func<long>? clock=null){this.config=config;this.provider=provider;this.connected=connected;this.log=log;this.clock=clock??(()=>Environment.TickCount64);state=config.Current.LlmKeepAliveEnabled?"waiting":"disabled";stop=CancellationTokenSource.CreateLinkedTokenSource(lifetime);config.Changed+=Changed;loop=start?Task.Run(Loop):Task.CompletedTask;}
    public KeepAliveStatus Status{get{lock(gate){var s=config.Current;return new(s.LlmKeepAliveEnabled,s.LlmKeepAliveSeconds,state,requests,successes,lastAt,lastElapsed,lastHttp,models);}}}
    private void Changed(){Interlocked.Increment(ref revision);provider.CancelKeepAlive();lock(gate){due.Clear();failures.Clear();state=config.Current.LlmKeepAliveEnabled?"waiting":"disabled";}}
    private async Task Loop(){try{while(!stop.IsCancellationRequested){await Task.Delay(5000,stop.Token);await Tick();}}catch(OperationCanceledException)when(stop.IsCancellationRequested){}}
    public Task<KeepAliveStatus> WarmOnce(CancellationToken cancellation=default)=>Tick(true,cancellation);
    public async Task<KeepAliveStatus> Tick(bool manual=false,CancellationToken cancellation=default){
        if(!await runGate.WaitAsync(0,cancellation))return Status;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(stop.Token,cancellation);
        try{
            var generation=Interlocked.Read(ref revision);var s=config.Current;var candidates=new List<string>();
            if(s.Enabled&&s.Mode!="fast")candidates.Add(LlmProfiles.Effective(s,false).Model);
            if(s.OutgoingEnabled&&s.OutgoingMode!="fast")candidates.Add(LlmProfiles.Effective(s,true).Model);
            candidates=candidates.Distinct(StringComparer.Ordinal).ToList();lock(gate)models=candidates.Count;
            string? skip=!manual&&!s.LlmKeepAliveEnabled?"disabled":!provider.HasKeepAliveCredentials?"unconfigured":!manual&&!connected()?"waiting-for-game":candidates.Count==0?"unused":null;
            if(skip!=null){lock(gate)state=skip;return Status;}
            var now=clock();lock(gate){if(now-lastAttempt<60000){state="waiting";return Status;}}
            foreach(var model in candidates){
                linked.Token.ThrowIfCancellationRequested();now=clock();
                if(generation!=Interlocked.Read(ref revision))break;
                lock(gate){if(!manual&&(now<due.GetValueOrDefault(model)||provider.LastModelActivity(model)>0&&now-provider.LastModelActivity(model)<s.LlmKeepAliveSeconds*1000L))continue;state="warming";}
                var result=await provider.TryKeepAlive(s,model,linked.Token);
                lock(gate){
                    state=result.State;
                    if(result.Attempted){requests++;lastAttempt=clock();lastAt=DateTimeOffset.UtcNow;lastElapsed=result.ElapsedMs;lastHttp=result.HttpStatus;}
                    if(result.State=="succeeded"){successes++;failures.Remove(model);due[model]=clock()+s.LlmKeepAliveSeconds*1000L;}
                    else if(result.State is "interrupted" or "cancelled" or "configuration-changed")due[model]=clock()+s.LlmKeepAliveSeconds*1000L;
                    else if(result.State=="unauthorized")due[model]=long.MaxValue;
                    else if(result.Attempted){var count=Math.Min(4,failures.GetValueOrDefault(model)+1);failures[model]=count;due[model]=clock()+Math.Min(1800000L,Math.Max(result.RetryAfterSeconds*1000L,s.LlmKeepAliveSeconds*1000L*(1L<<count)));}
                }
                if(result.Attempted)log?.Record("translation",LogKind.LlmKeepAlive,new(Reason:result.Reason,Stage:"keep-alive",Provider:"quality",HttpStatus:result.HttpStatus,ElapsedMs:result.ElapsedMs,Flag:result.State=="succeeded"));
                if(result.State is "busy" or "interrupted" or "cancelled" or "configuration-changed" or "rate-limited" or "unauthorized")break;
                // A real request may have arrived while the previous distinct model was warming.
                if(!manual&&!connected())break;
            }
            lock(gate)if(state=="warming")state="waiting";
            return Status;
        }finally{runGate.Release();}
    }
    public async ValueTask DisposeAsync(){config.Changed-=Changed;stop.Cancel();provider.CancelKeepAlive();await loop;await runGate.WaitAsync();runGate.Release();runGate.Dispose();stop.Dispose();}
}
