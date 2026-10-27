using System.Net.Http.Headers;
using System.Text.Json;

namespace DeadlockTranslate;

public sealed partial class Translators : ILlmKeepAliveProvider {
    private readonly object warmGate=new();
    private readonly Dictionary<string,long> modelActivity=new(StringComparer.Ordinal);
    private WarmFlight? warmFlight;
    public bool HasKeepAliveCredentials=>store.Secret("llm").Length>0;
    public long LastModelActivity(string model){lock(warmGate)return modelActivity.GetValueOrDefault(model);}
    private void TouchModel(string? model){if(model==null)return;lock(warmGate){if(modelActivity.Count>32)modelActivity.Clear();modelActivity[model]=Environment.TickCount64;}}
    private sealed class WarmFlight(CancellationTokenSource cancellation,IDisposable lease) {
        private int preempted;
        public bool Preempted=>Volatile.Read(ref preempted)!=0;
        public CancellationTokenSource Cancellation {get;}=cancellation;
        public IDisposable Lease {get;}=lease;
        public void Cancel(){Interlocked.Exchange(ref preempted,1);try{Cancellation.Cancel();}catch(ObjectDisposedException){}catch(AggregateException){}}
    }
    public void CancelKeepAlive(){WarmFlight? previous;lock(warmGate){previous=warmFlight;warmFlight=null;previous?.Lease.Dispose();}previous?.Cancel();}
    private async Task<IDisposable> EnterRealLlm(CancellationToken ct,bool priority,string? model){
        ct.ThrowIfCancellationRequested();Task<IDisposable> pending;WarmFlight? previous=null;
        try{lock(warmGate){previous=warmFlight;warmFlight=null;previous?.Lease.Dispose();TouchModel(model);pending=llmSlots.Enter(ct,priority);}}
        finally{previous?.Cancel();}
        var lease=await pending.ConfigureAwait(false);return new ActivityLease(this,lease,model);
    }
    private sealed class ActivityLease(Translators owner,IDisposable lease,string? model):IDisposable {
        private IDisposable? current=lease;
        public void Dispose(){var held=Interlocked.Exchange(ref current,null);if(held==null)return;owner.TouchModel(model);held.Dispose();}
    }
    public async Task<KeepAliveOutcome> TryKeepAlive(Settings settings,string model,CancellationToken cancellation){
        if(!HasKeepAliveCredentials)return new("unconfigured");
        try{CheckCooldown("llm");}catch(InvalidOperationException){return new("cooldown",LogReason.RateLimit);}
        var timer=System.Diagnostics.Stopwatch.StartNew();WarmFlight flight;
        lock(warmGate){
            if(warmFlight!=null)return new("busy");
            var lease=llmSlots.TryEnterIdle();if(lease==null)return new("busy");
            var cts=CancellationTokenSource.CreateLinkedTokenSource(cancellation);cts.CancelAfter(TimeSpan.FromSeconds(10));
            flight=new(cts,lease);warmFlight=flight;
        }
        var attempted=false;
        try{
            flight.Cancellation.Token.ThrowIfCancellationRequested();
            using var request=new HttpRequestMessage(HttpMethod.Post,ApiConfigurationDiagnostics.ChatEndpoint(settings.LlmBaseUrl));
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",store.LlmSecretFor(settings));
            var body=new Dictionary<string,object>{["model"]=model,["stream"]=false,["max_tokens"]=16,["messages"]=new[]{new{role="user",content="Reply only OK."}}};
            // Maintenance is not a translation: do not spend the user's reasoning budget on ping.
            // Actual receive/send requests retain their independently configured thinking settings.
            if(LlmRequestOptions.AppliedThinking(settings.LlmBaseUrl,model,settings.LlmThinkingApi,false).HasValue)body["thinking"]=new{type="disabled"};
            request.Content=JsonContent.Create(body);flight.Cancellation.Token.ThrowIfCancellationRequested();attempted=true;
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,flight.Cancellation.Token).ConfigureAwait(false);
            var status=(int)response.StatusCode;
            if(!response.IsSuccessStatusCode){
                var retry=response.Headers.RetryAfter?.Delta?.TotalSeconds??(response.Headers.RetryAfter?.Date-DateTimeOffset.UtcNow)?.TotalSeconds??300;
                return new(status==429?"rate-limited":status is 401 or 403?"unauthorized":"failed",status==429?LogReason.RateLimit:status is 401 or 403?LogReason.Unauthorized:LogReason.Network,status,(int)Math.Clamp(retry,60,1800),true,timer.ElapsedMilliseconds);
            }
            await response.Content.LoadIntoBufferAsync(65536,flight.Cancellation.Token).ConfigureAwait(false);
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(flight.Cancellation.Token));
            if(json.RootElement.ValueKind!=JsonValueKind.Object||!json.RootElement.TryGetProperty("choices",out var choices)||choices.ValueKind!=JsonValueKind.Array||choices.GetArrayLength()==0)return new("failed",LogReason.InvalidData,status,Attempted:true,ElapsedMs:timer.ElapsedMilliseconds);
            return new("succeeded",HttpStatus:status,Attempted:true,ElapsedMs:timer.ElapsedMilliseconds);
        }catch(OperationCanceledException){return new(cancellation.IsCancellationRequested?"cancelled":flight.Preempted?"interrupted":"failed",flight.Preempted?LogReason.None:LogReason.Timeout,Attempted:attempted,ElapsedMs:timer.ElapsedMilliseconds);}
        catch(InvalidOperationException){return new("configuration-changed",LogReason.InvalidData,Attempted:attempted,ElapsedMs:timer.ElapsedMilliseconds);}
        catch(Exception ex)when(ex is HttpRequestException or IOException or JsonException or ArgumentException){return new("failed",LogReason.Network,Attempted:attempted,ElapsedMs:timer.ElapsedMilliseconds);}
        finally{lock(warmGate){if(ReferenceEquals(warmFlight,flight))warmFlight=null;flight.Lease.Dispose();}flight.Cancellation.Dispose();}
    }
}
