namespace DeadlockTranslate;

// A reserved send slot keeps a receive burst from occupying the entire provider budget.
// With one slot, priority alternates after at most three sends to avoid starving receives.
public sealed class PriorityConcurrency(int limit, bool reserveSend) {
    private readonly object gate=new();
    private readonly Queue<TaskCompletionSource<IDisposable>> normal=new(), urgent=new();
    private int active,incoming,maximum=limit,urgentBurst;
    private bool reserve=reserveSend;
    public object Stats {get{lock(gate)return new{active,waiting=normal.Count(x=>!x.Task.IsCompleted)+urgent.Count(x=>!x.Task.IsCompleted),limit=maximum,incomingLimit=IncomingLimit,outgoingWaiting=urgent.Count(x=>!x.Task.IsCompleted)};}}
    private int IncomingLimit=>reserve&&maximum>1?maximum-1:maximum;
    public void Configure(int limit,bool reserveSend){if(limit<1)throw new ArgumentOutOfRangeException(nameof(limit));lock(gate){maximum=limit;reserve=reserveSend;Drain();}}
    public async Task<IDisposable> Enter(CancellationToken token,bool priority=false){
        token.ThrowIfCancellationRequested();
        var waiter=new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(gate){(priority?urgent:normal).Enqueue(waiter);Drain();}
        using var registration=token.Register(()=>{lock(gate){waiter.TrySetCanceled(token);Drain();}});
        return await waiter.Task.ConfigureAwait(false);
    }
    private static void Trim(Queue<TaskCompletionSource<IDisposable>> queue){while(queue.TryPeek(out var first)&&first.Task.IsCompleted)queue.Dequeue();}
    private void Drain(){
        while(active<maximum){
            Trim(normal);Trim(urgent);
            bool canNormal=normal.Count>0&&incoming<IncomingLimit;
            bool priority=urgent.Count>0&&(!canNormal||urgentBurst<3);
            if(!priority&&!canNormal)return;
            var waiter=(priority?urgent:normal).Dequeue();
            if(waiter.TrySetResult(new Lease(this,priority))){active++;if(priority)urgentBurst++;else{incoming++;urgentBurst=0;}}
        }
    }
    private void Release(bool priority){lock(gate){active--;if(!priority)incoming--;Drain();}}
    private sealed class Lease(PriorityConcurrency owner,bool priority):IDisposable{
        private PriorityConcurrency? current=owner;
        public void Dispose()=>Interlocked.Exchange(ref current,null)?.Release(priority);
    }
}
