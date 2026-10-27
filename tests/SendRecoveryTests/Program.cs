using DeadlockTranslate;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var folder=Path.Combine(root,"artifacts/send-fixes-20261024/service-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
var config=new ConfigStore(folder);config.Save(config.Current with{LlmKey="PRIVATE_TEST_KEY",LlmBaseUrl="https://fixture.invalid/v1",Model="fixture",OutgoingMode="quality",LlmStreaming=false,LlmTimeoutSeconds=20,OutgoingTimeoutSeconds=20});
var terms=new Terms(folder,Path.Combine(root,"src/Bridge/data/terms.json"));
await using var log=new DiagnosticLog(folder);
var passed=0;void Check(bool ok,string name){if(!ok)throw new Exception("FAIL "+name);passed++;Console.WriteLine("PASS "+name);}
HttpRequestException Connect(SocketError code)=>new(HttpRequestError.ConnectionError,"PRIVATE_URL_OR_PROXY",new SocketException((int)code));
foreach(var code in new[]{SocketError.ConnectionRefused,SocketError.NetworkUnreachable,SocketError.HostUnreachable})Check(ProviderNetworkFailure.CanRetryBeforeResponse(Connect(code)),"pre-connection failure may retry once: "+code);
foreach(var code in new[]{SocketError.ConnectionReset,SocketError.ConnectionAborted,SocketError.TimedOut})Check(!ProviderNetworkFailure.CanRetryBeforeResponse(Connect(code)),"uncertain connection failure never replays: "+code);
Check(!ProviderNetworkFailure.CanRetryBeforeResponse(new(HttpRequestError.SecureConnectionError,"private")),"TLS failures never bypass verification or replay");
Check(!ProviderNetworkFailure.CanRetryBeforeResponse(new(HttpRequestError.ProxyTunnelError,"private")),"proxy tunnel failures have guidance instead of blind replay");
Check(ApiConfigurationDiagnostics.NetworkFailure(Connect(SocketError.TimedOut)).Code=="connect-timeout","socket timeout is distinguished from model token exhaustion");
Check(ApiConfigurationDiagnostics.NetworkFailure(new HttpRequestException(HttpRequestError.NameResolutionError,"private")).Code=="dns","DNS guidance identifies the address/network stage");

async Task<(string? Result,Exception? Error,Handler Handler)> Attempt(string id,Func<int,CancellationToken,Task<HttpResponseMessage>> send,CancellationToken token=default){
    var handler=new Handler(send);using var http=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};using var service=new Translators(config,terms,http,log);
    try{return(await service.TranslateOutgoing("测试 "+id,config.Current,false,token),null,handler);}catch(Exception e){return(null,e,handler);}
}
HttpResponseMessage Reply()=>new(HttpStatusCode.OK){Content=JsonContent.Create(new{choices=new[]{new{finish_reason="stop",message=new{content="test message"}}}})};
var recovered=await Attempt("dns",(n,ct)=>n==1?Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.NameResolutionError,"PRIVATE_DOMAIN")):Task.FromResult(Reply()));
Check(recovered.Result=="test message"&&recovered.Handler.Calls==2,"a transient DNS failure recovers without a second game send");
Check(recovered.Handler.Bodies[0]==recovered.Handler.Bodies[1],"network reconnect preserves model, thinking and exact request body");
var down=await Attempt("refused",(n,ct)=>Task.FromException<HttpResponseMessage>(Connect(SocketError.ConnectionRefused)));
Check(down.Error is ProviderNetworkException&&down.Handler.Calls==2,"persistent pre-connection failures stop after one retry");
Check(!down.Error!.Message.Contains("PRIVATE_")&&down.Error.Message.Contains("网络"),"network guidance contains no provider exception text or URL");
var reset=await Attempt("reset",(n,ct)=>Task.FromException<HttpResponseMessage>(Connect(SocketError.ConnectionReset)));
Check(reset.Handler.Calls==1&&reset.Error is ProviderNetworkException,"uncertain connection resets are not sent twice");
var tls=await Attempt("tls",(n,ct)=>Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.SecureConnectionError,"PRIVATE_CERT")));
Check(tls.Handler.Calls==1&&tls.Error?.Message.Contains("HTTPS")==true,"TLS errors preserve security and show relevant repair steps");
var proxy=await Attempt("proxy",(n,ct)=>Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ProxyTunnelError,"PRIVATE_PROXY")));
Check(proxy.Handler.Calls==1&&proxy.Error?.Message.Contains("代理")==true,"proxy failures are identified without exposing the proxy endpoint");
var denied=await Attempt("unauthorized",(n,ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized){Content=JsonContent.Create(new{error=new{message="PRIVATE_PROVIDER_RESPONSE"}})}));
Check(denied.Handler.Calls==1&&denied.Error is ProviderRequestException,"authorization errors never trigger a network replay");
var stream=await Attempt("mid-stream",(n,ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n",Encoding.UTF8,"text/event-stream")}));
Check(stream.Handler.Calls==1&&stream.Error is ProviderNetworkException e&&e.HttpRequestError==HttpRequestError.ResponseEnded,"an interrupted stream is never retried or accepted as a complete answer");
var slow=await Attempt("six-seconds",async(n,ct)=>{await Task.Delay(5200,ct);return Reply();});
Check(slow.Result=="test message"&&slow.Handler.Calls==1,"outgoing API requests have no accidental five-second response deadline");
using(var timeout=new CancellationTokenSource(50)){
    var cancelled=await Attempt("cancel",async(n,ct)=>{await Task.Delay(500,ct);return Reply();},timeout.Token);
    Check(cancelled.Error is OperationCanceledException&&cancelled.Handler.Calls==1,"caller cancellation prevents retry and stale completion");
}
await using(var outgoing=new OutgoingService(config,(text,c,fast,ct)=>Task.FromException<string>(new ProviderNetworkException("llm","llm-connect",Connect(SocketError.ConnectionRefused))),log:log)){
    outgoing.Exchange("fixture",[new("failed-task","PRIVATE_CHAT","team")],[]);
    for(var i=0;i<100&&outgoing.Recent.Single().State!="failed";i++)await Task.Delay(10);
    var view=outgoing.Recent.Single();Check(view.State=="failed"&&view.Original=="PRIVATE_CHAT"&&view.Error?.Contains("网络")==true,"network-failed outgoing task retains its original and actionable guidance");
    Check(outgoing.Diagnostics.NetworkError=="ConnectionError"&&outgoing.Diagnostics.SocketError==(int)SocketError.ConnectionRefused,"settings diagnostics expose a fixed network category and numeric socket code");
}
await log.FlushAsync();var logs=string.Concat((await log.SnapshotAsync()).Select(Encoding.UTF8.GetString));
Check(logs.Contains("provider-retry")&&logs.Contains("ConnectionError")&&logs.Contains("socketError")&&logs.Contains("llm-connect"),"safe diagnostics record retries, network category, socket code and stage");
Check(!logs.Contains("PRIVATE_")&&!logs.Contains("fixture.invalid"),"diagnostic export excludes chat, API credentials and private endpoints");
var cleaned=DiagnosticLog.Clean(new(NetworkError:"PRIVATE_URL",SocketError:-1));Check(cleaned.NetworkError==null&&cleaned.SocketError==null,"invalid diagnostic network strings and socket values are rejected");
File.WriteAllText(Path.Combine(root,"artifacts/send-fixes-20261024/network-tests.json"),JsonSerializer.Serialize(new{passed,cloudCalls=0,realGameSend=false},ConfigStore.Json));
Console.WriteLine("TOTAL "+passed);

sealed class Handler(Func<int,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler {
    public int Calls;public List<string> Bodies=[];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Bodies.Add(await request.Content!.ReadAsStringAsync(token));return await send(++Calls,token);}
}
