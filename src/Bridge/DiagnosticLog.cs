using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace DeadlockTranslate;

public enum LogKind { LlmKeepAlive, GameMetrics, HotkeySummary, Started, Stopped, AdminHandoff, PrivilegeMismatch, StartupFailed, ConfigSaved, ClientConnected, ClientLost, ClientVersionMismatch, GameEvent, TranslationCompleted, TranslationFailed, SendQueued, SendPhase, SendFailed, HotkeyInstalled, HotkeyInstallFailed, HotkeyTriggered, HotkeyRejected, HotkeyAcknowledged, HotkeyAckTimeout, HotkeyTest, HotkeyReinitialized, RequestRejected, ExportCreated, ExportFailed, HotkeyRecordingStarted, HotkeyRecordingFinished, TranslationTiming, LlmUsage }
public enum LogReason { None, Timeout, Network, Unauthorized, RateLimit, InvalidData, NoChat, EmptyInput, UnknownChannel, QueueFull, Disabled, StaleBridge, ForegroundUnknown, ForegroundOther, ElevatedGame, ElevationRequired, UacCancelled, PairingMissing, InstanceMismatch, ScopeMismatch, PortBusy, PortTimeout, DifferentUser, HandoffTimeout, Injected, ModifiersMismatch, ClientOld, NativeCloseFailed, ServerChanged, DiskFailure, Other, ServiceRedirect, ModelUnavailable, EndpointInvalid, QuotaExceeded, RegionMismatch, ParametersRejected, ReasoningBudget, OutputTruncated, NativeInputTruncated, NameMismatch, DisplayTemplateMissing, DisplayGeometryUnavailable,DisplayHidden, DisplayContainerMissing, DisplayHostMissing, DisplayDetached, DisplayPanelGone, DisplayNativeExpired, DisplayMessageChanged, DisplayRetryExpired }
// No message text, sender name, URL, key, exception message or stack trace fields exist.
public sealed record LogFields(LogReason Reason=LogReason.None,string? Token=null,string? Stage=null,string? Channel=null,string? Provider=null,string? Binding=null,string? Version=null,string? FailureType=null,int? HttpStatus=null,int? Win32Error=null,int? Count=null,double? ElapsedMs=null,bool? Flag=null,int? InputTokens=null,int? OutputTokens=null,int? ReasoningTokens=null,int? MaxTokens=null,string? FinishReason=null,double? FirstResponseMs=null,double? FirstContentMs=null,bool? Thinking=null,bool? Streaming=null,GameMetricSummary? Counters=null,HudLayoutEvidence? Layout=null,HudAncestorEvidence[]? Ancestry=null,FeedLayoutEvidence? Feed=null,string? BuildId=null,SendLengthEvidence? SendLength=null,string? NetworkError=null,int? SocketError=null);
public sealed record SafeLogEvent(DateTimeOffset At,string RunId,string Component,LogKind Event,LogFields Data);
public sealed record LogStorageStatus(bool Available,long DroppedEvents,long WriteFailures,string? FailureCode,int MaximumFiles,long MaximumFileBytes);

public sealed class DiagnosticLog : IAsyncDisposable {
    private sealed record Item(SafeLogEvent? Event,TaskCompletionSource? Barrier=null,TaskCompletionSource<byte[][]>? Snapshot=null);
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull,Converters={new JsonStringEnumConverter()}};
    private readonly Channel<Item> queue;
    private readonly Task worker;
    private readonly int maxFiles;private readonly long maxBytes;
    private FileStream? stream;private long dropped,failures;private volatile bool available;private string? failureCode;
    private int disposed;
    public string DirectoryPath {get;}
    public string RunId {get;}=Guid.NewGuid().ToString("N");
    public LogStorageStatus Status=>new(available,Interlocked.Read(ref dropped),Interlocked.Read(ref failures),failureCode,maxFiles,maxBytes);
    public DiagnosticLog(string stateDirectory,long maximumFileBytes=1024*1024,int maximumFiles=4,int capacity=1024){
        if(maximumFileBytes<256||maximumFiles is <1 or >8||capacity<1)throw new ArgumentOutOfRangeException();
        DirectoryPath=Path.Combine(stateDirectory,"logs");maxFiles=maximumFiles;maxBytes=maximumFileBytes;
        queue=Channel.CreateBounded<Item>(new BoundedChannelOptions(capacity){FullMode=BoundedChannelFullMode.Wait,SingleReader=true,SingleWriter=false});
        worker=Task.Run(WriteLoop);
    }
    public static string Token(string session,string id)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session+"\n"+id)))[..16].ToLowerInvariant();
    public void Record(string component,LogKind kind,LogFields? fields=null){
        if(Volatile.Read(ref disposed)!=0)return;
        if(component is not ("backend" or "game" or "hotkey" or "translation" or "sending" or "export"))component="backend";
        var f=fields??new();
        if(!queue.Writer.TryWrite(new(new(DateTimeOffset.UtcNow,RunId,component,kind,f))))Interlocked.Increment(ref dropped);
    }
    public static LogFields Clean(LogFields f){
        // Defensive schema validation also protects export readers from accidental new call sites.
        var token=f.Token is {Length:16}&&f.Token.All(Uri.IsHexDigit)?f.Token:null;
        var binding=HotkeyChord.TryParse(f.Binding,out var chord)?chord!.Canonical:null;
        int? Count(int? n)=>n is >=0 and <=2000000?n:null;double? Time(double? n)=>n is double d&&double.IsFinite(d)?Math.Clamp(d,0,86400000):null;
        return f with{NetworkError=f.NetworkError!=null&&Enum.TryParse<HttpRequestError>(f.NetworkError,out var network)&&Enum.IsDefined(network)?network.ToString():null,SocketError=f.SocketError is >=0 and <=65535?f.SocketError:null,SendLength=f.SendLength?.Clean(),BuildId=f.BuildId is {Length:>0 and <=40}&&f.BuildId.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or '-')?f.BuildId:null,Feed=f.Feed?.Clean(),Ancestry=HudAncestorEvidence.Clean(f.Ancestry),Layout=f.Layout?.Clean(),Counters=f.Counters?.Clean(),InputTokens=Count(f.InputTokens),OutputTokens=Count(f.OutputTokens),ReasoningTokens=Count(f.ReasoningTokens),MaxTokens=Count(f.MaxTokens),FinishReason=f.FinishReason is "stop" or "length" or "content_filter" or "tool_calls" or "function_call" or "other"?f.FinishReason:null,FirstResponseMs=Time(f.FirstResponseMs),FirstContentMs=Time(f.FirstContentMs),Reason=Enum.IsDefined(f.Reason)?f.Reason:LogReason.Other,Token=token,Stage=Stages.Contains(f.Stage??"")?f.Stage:null,Channel=f.Channel is "team" or "all" or "party" or "hud" or "lobby" or "unknown"?f.Channel:null,Provider=f.Provider is "fast" or "quality" or "bing" or "azure"?f.Provider:null,Binding=binding,Version=f.Version is {Length:>0 and <=32}&&f.Version.All(c=>char.IsAsciiDigit(c)||c=='.')?f.Version:null,FailureType=Failures.Contains(f.FailureType??"")?f.FailureType:f.FailureType==null?null:"Other",ElapsedMs=f.ElapsedMs is double d&&double.IsFinite(d)?Math.Clamp(d,0,86400000):null};
    }
    private static readonly HashSet<string> Stages=new(["provider-retry","names-repair","llm-connect","llm-response","send-echo-observed","send-echo-unobserved","waiting_input","dispatched","echo_observed","echo_unobserved","send-close-request","send-close-confirmed","send-close-interrupted","send-close-timeout","send-close-retry","send-draft-open","send-draft-unavailable","send-draft-queued","send-unconfirmed","channel-cached","settings-background","settings-input","keep-alive","hud-nudge-start","hud-nudge-recovered","hud-nudge-failed","feed-layout","feed-restored","own-hidden","own-display","hud-root-reflow","hud-native-born","hud-local-receipt","hud-direct-applied","hud-native-write-failed","hud-context-applied","hud-context-unavailable","hud-context-rejected","hud-ancestry-sample","hud-template-paint","hud-template-paint-missing","hud-template-render-state","hud-template-api","hud-template-bound","hud-template-layout","hud-anchor-mounted","hud-anchor-layout","hud-anchor-unavailable","hud-anchor-hidden","hud-anchor-retired","hud-anchor-displaced","hud-layout-nudge","hud-layout-stalled","hud-layout-before","hud-layout-after","hud-refresh","hud-refresh-unavailable","hero-roster","hero-resolved","hero-unavailable","hud-wait","hud-retired","hud-restored","hud-retry-ended","feed-mounted","feed-unavailable","feed-written","feed-ended","feed-displaced","display-mode","bridge-bound","bridge-rebuilt","bridge-reconnect","adapter-loop-resumed","adapter-root-moved","hud-bound","hud-written","hud-expired","hud-host-repaired","hud-hidden-repaired","metrics-sample","metrics-session-end","metrics-reset","metrics-stop","hotkey-summary","hud-rebound","llm-usage","term-prepare","provider-queue","provider-response","names-local","recording","api-config-test","bing-page","bing-translate","azure-translate","llm-translate","llm-output","provider-request","provider-test","startup","elevation","instance","handoff","steam","settings","standalone","certificate","export","fast","quality","pending","queued","translating","ready","submitted","failed","waiting_channel","waiting_order","captured","rejected","adapter-start","session-reset","bridge-timeout","bridge-error","bridge-reset","transport-fallback","menu-mounted","menu-failed","settings-open","settings-close","hotkey-captured","hotkey-dropped","channel-wait","channel-fault","send-submitted","send-failed","chat-open","chat-closed"]);
    private static readonly HashSet<string> Failures=new(["ProviderNetworkException","TranslationOutputException","ProviderRequestException","ArgumentException","JsonException","IOException","InvalidDataException","CryptographicException","InvalidOperationException","HttpRequestException","UnauthorizedAccessException","TaskCanceledException","OperationCanceledException","Win32Exception","StartupFault","Other"]);
    public async Task FlushAsync(CancellationToken token=default){
        if(Volatile.Read(ref disposed)!=0){await worker.WaitAsync(token);return;}
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new(null,done),token);await done.Task.WaitAsync(token);
    }
    public string FilePath(int index)=>Path.Combine(DirectoryPath,index==0?"runtime-current.jsonl":$"runtime-{index}.jsonl");
    private void CheckPath(string path){
        var current=Path.GetFullPath(path);
        while(current!=null){if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Log path contains link");current=Path.GetDirectoryName(current);}
    }
    private void Open(){CheckPath(DirectoryPath);Directory.CreateDirectory(DirectoryPath);for(var i=0;i<maxFiles;i++){var p=FilePath(i);CheckPath(p);if(File.Exists(p)&&new FileInfo(p).Length>maxBytes){using var oversized=new FileStream(p,FileMode.Open,FileAccess.Write,FileShare.Read);oversized.SetLength(maxBytes);}}stream=new(FilePath(0),FileMode.Append,FileAccess.Write,FileShare.Read,16384,FileOptions.Asynchronous|FileOptions.SequentialScan);available=true;failureCode=null;}
    private void Rotate(){
        stream?.Dispose();stream=null;
        for(var i=maxFiles-1;i>=0;i--){var path=FilePath(i);CheckPath(path);if(!File.Exists(path))continue;if(i==maxFiles-1)File.Delete(path);else File.Move(path,FilePath(i+1),true);}
        Open();
    }
    private void Fail(){available=false;failureCode="log-write-failed";Interlocked.Increment(ref failures);try{stream?.Dispose();}catch{}stream=null;}
    private async Task WriteLoop(){
        var nextRetry=0L;
        try{
            await foreach(var item in queue.Reader.ReadAllAsync()){
                if(item.Barrier!=null){try{if(stream!=null){await stream.FlushAsync();stream.Flush(true);}}catch{Fail();}finally{item.Barrier.TrySetResult();}continue;}
                if(item.Snapshot!=null){try{if(stream!=null){await stream.FlushAsync();stream.Flush(true);}item.Snapshot.TrySetResult(await ReadSnapshot());}catch{Fail();item.Snapshot.TrySetResult([]);}continue;}
                try{
                    if(stream==null){if(Environment.TickCount64<nextRetry){Interlocked.Increment(ref dropped);continue;}Open();}
                    var raw=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item.Event! with{Data=Clean(item.Event!.Data)},Json)+"\n");
                    if(raw.Length>4096||raw.Length>maxBytes){Interlocked.Increment(ref dropped);continue;}
                    if(stream!.Length+raw.Length>maxBytes)Rotate();
                    await stream!.WriteAsync(raw);
                    // Flush the userspace buffer, on the worker only. No disk work in hook/bridge callbacks.
                    if(!queue.Reader.TryPeek(out _))await stream.FlushAsync();
                }catch{Fail();nextRetry=Environment.TickCount64+5000;Interlocked.Increment(ref dropped);}
            }
            if(stream!=null){await stream.FlushAsync();stream.Flush(true);}
        }catch{Fail();}finally{queue.Writer.TryComplete();while(queue.Reader.TryRead(out var remaining)){remaining.Barrier?.TrySetResult();remaining.Snapshot?.TrySetResult([]);}try{stream?.Dispose();}catch{}stream=null;}
    }
    public async Task<byte[][]> SnapshotAsync(CancellationToken token=default){
        if(Volatile.Read(ref disposed)!=0){await worker.WaitAsync(token);return await ReadSnapshot();}
        var done=new TaskCompletionSource<byte[][]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new(null,Snapshot:done),token);return await done.Task.WaitAsync(token);
    }
    private async Task<byte[][]> ReadSnapshot(){
        var result=new List<byte[]>();
        // Snapshot runs on the sole writer, so rotation cannot race an export.
        for(var i=0;i<maxFiles;i++){try{var p=FilePath(i);CheckPath(p);if(!File.Exists(p))continue;using var f=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);if(f.Length>maxBytes)continue;var bytes=new byte[(int)f.Length];var count=0;while(count<bytes.Length){var n=await f.ReadAsync(bytes.AsMemory(count));if(n==0)break;count+=n;}result.Add(bytes[..count]);}catch(IOException){}catch(UnauthorizedAccessException){}}
        return result.ToArray();
    }
    public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref disposed,1)!=0)return;queue.Writer.TryComplete();await worker;}
}
