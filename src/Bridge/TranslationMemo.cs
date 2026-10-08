namespace DeadlockTranslate;

// Memory-only exact-context cache. Each consumer may cancel without cancelling other consumers.
public sealed class TranslationMemo : IDisposable {
    private readonly object gate=new();
    private readonly Dictionary<string,(long Until,string Text)> cache=new();
    private readonly Dictionary<string,Flight> flights=new();
    private long hits,joined;private bool disposed;
    private sealed class Flight {
        public readonly CancellationTokenSource Stop=new();
        public readonly TaskCompletionSource<string> Result=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Users;
    }
    public object Stats {get{lock(gate)return new{hits,joined,cached=cache.Count,inFlight=flights.Count};}}
    public async Task<string> Get(string key,TimeSpan timeout,Func<CancellationToken,Task<string>> factory,CancellationToken token){
        token.ThrowIfCancellationRequested();Flight flight;bool start=false;
        lock(gate){
            ObjectDisposedException.ThrowIf(disposed,this);
            if(cache.TryGetValue(key,out var hit)&&hit.Until>Environment.TickCount64){hits++;return hit.Text;}
            if(!flights.TryGetValue(key,out flight!)){flight=new();flights.Add(key,flight);start=true;}else joined++;
            flight.Users++;
        }
        if(start)_=Run(key,flight,timeout,factory);
        try{return await flight.Result.Task.WaitAsync(token).ConfigureAwait(false);}
        finally{
            lock(gate){
                flight.Users--;
                if(flight.Users==0&&!flight.Result.Task.IsCompleted){
                    if(flights.TryGetValue(key,out var current)&&ReferenceEquals(current,flight))flights.Remove(key);
                    flight.Stop.Cancel();
                }
            }
        }
    }
    private async Task Run(string key,Flight flight,TimeSpan timeout,Func<CancellationToken,Task<string>> factory){
        try{
            flight.Stop.CancelAfter(timeout);
            var value=await factory(flight.Stop.Token).ConfigureAwait(false);
            flight.Stop.Token.ThrowIfCancellationRequested();
            if(string.IsNullOrWhiteSpace(value)||value.Length>2500)throw new InvalidDataException("Empty or oversized translation");
            lock(gate){
                if(!disposed&&!flight.Stop.IsCancellationRequested){
                    foreach(var old in cache.Where(p=>p.Value.Until<=Environment.TickCount64).Select(p=>p.Key).ToArray())cache.Remove(old);
                    if(cache.Count>=256)cache.Remove(cache.MinBy(p=>p.Value.Until).Key);
                    cache[key]=(Environment.TickCount64+300000,value);
                }
                flight.Result.TrySetResult(value);
            }
        }catch(OperationCanceledException){flight.Result.TrySetCanceled();}
        catch(Exception e){flight.Result.TrySetException(e);}
        finally{
            lock(gate){if(flights.TryGetValue(key,out var current)&&ReferenceEquals(current,flight))flights.Remove(key);flight.Stop.Dispose();}
            // Observe errors even if every subscriber cancelled before the shared request finished.
            if(flight.Result.Task.IsFaulted)_=flight.Result.Task.Exception;
        }
    }
    public void Clear(){lock(gate)cache.Clear();}
    public void Dispose(){lock(gate){disposed=true;cache.Clear();foreach(var f in flights.Values.ToArray())f.Stop.Cancel();}}
}
