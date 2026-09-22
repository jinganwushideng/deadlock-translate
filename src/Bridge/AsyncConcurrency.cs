namespace DeadlockTranslate;

// FIFO asynchronous admission. Changing the limit never cancels a running request.
public sealed class AsyncConcurrency(int limit) {
    private readonly object gate=new();
    private readonly Queue<TaskCompletionSource<IDisposable>> waiting=new();
    private int active,maximum=limit;
    public object Stats {get{lock(gate)return new{active,waiting=waiting.Count(x=>!x.Task.IsCompleted),limit=maximum};}}
    public void SetLimit(int value){if(value<1)throw new ArgumentOutOfRangeException(nameof(value));lock(gate){maximum=value;Drain();}}
    public async Task<IDisposable> Enter(CancellationToken token){
        token.ThrowIfCancellationRequested();
        TaskCompletionSource<IDisposable> waiter;
        lock(gate){
            if(active<maximum && waiting.Count==0){active++;return new Lease(this);}
            waiter=new(TaskCreationOptions.RunContinuationsAsynchronously);waiting.Enqueue(waiter);Drain();
        }
        using var cancellation=token.Register(()=>waiter.TrySetCanceled(token));
        return await waiter.Task.ConfigureAwait(false);
    }
    private void Drain(){
        while(active<maximum && waiting.TryDequeue(out var waiter)){
            if(waiter.TrySetResult(new Lease(this)))active++;
        }
    }
    private void Release(){lock(gate){active--;Drain();}}
    private sealed class Lease(AsyncConcurrency owner):IDisposable {
        private AsyncConcurrency? current=owner;
        public void Dispose()=>Interlocked.Exchange(ref current,null)?.Release();
    }
}
