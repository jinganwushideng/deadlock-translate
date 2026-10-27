using DeadlockTranslate;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.IO.Compression;

int passed=0;void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));var folder=Path.Combine(root,"artifacts","api-config-tests-"+Guid.NewGuid().ToString("N"));
var config=new ConfigStore(folder);config.Save(config.Current with{LlmBaseUrl="https://api.example.test/v1",Model="saved-model",LlmKey="fixture-private-key",AzureKey="fixture-azure-key",AzureRegion="eastasia",OutgoingMode="quality"});
ApiConfigurationReport Inspect(string? url=null,string? model=null,string? key=null,bool saved=true)=>ApiConfigurationDiagnostics.Inspect(config,new("llm",url,model,key,UseSavedKey:saved));
Check(Inspect().Ready&&Inspect().Endpoint=="https://api.example.test/v1/chat/completions"&&Inspect().KeySource=="saved","saved credentials and actual endpoint are explained without returning the key");
Check(Inspect(url:"https://api.example.test/v1/chat/completions").Endpoint=="https://api.example.test/v1/chat/completions","complete endpoint is not appended twice");
foreach(var url in new[]{"api.example.test/v1","http://api.example.test/v1","https://u:secret@api.example.test/v1","https://api.example.test/v1?key=secret","https://api.example.test/v1#secret","https://api.example.test/console","https://api.example.test/anthropic","https://api.example.test/responses","https://api.example.test/v1/models","https://api.example.test/chat/completions/chat/completions","https://api.example.test/v1复制","https://api.example.test/v1\u200b"})Check(Inspect(url:url).Checks.Any(c=>c.Field=="address"&&c.Level=="error"),"wrong address/protocol is identified locally");
Check(Inspect(url:"https://other.example.test/v1").Checks.Any(c=>c.Code=="key-service-changed"),"saved key is not implicitly sent to a different draft service");
Check(Inspect(url:"https://other.example.test/v1",key:"new-service-fixture-key").Ready,"explicit new-service key permits a draft test");
Check(Inspect(key:"Bearer fixture-private-key").Checks.Any(c=>c.Field=="key"&&c.Level=="error"),"copied Authorization header is distinguished from a key");
Check(Inspect(key:"fixture\nkey").Checks.Any(c=>c.Field=="key"&&c.Level=="error"),"hidden/newline key input is rejected");
Check(!Inspect(saved:false).Ready,"cleared key does not fall back to saved credentials");
Check(Inspect(model:"saved-model ").Checks.Any(c=>c.Code=="model-hidden-space"),"trailing model space is a local input error");
Check(Inspect(model:"Model Display Name").Checks.Any(c=>c.Code=="model-display-name"&&c.Level=="warning"),"display-looking model names warn rather than claiming server invalidity");
var misplaced=Inspect(model:"fixture-private-key");Check(!misplaced.Ready&&misplaced.ModelId==null&&!JsonSerializer.Serialize(misplaced).Contains("fixture-private-key"),"key copied into model field is omitted from the diagnostic report");
Check(Inspect(url:"http://127.0.0.1:12345/v1",key:"local-fixture-key").Ready,"intentional local HTTP API remains supported");
Check(ApiConfigurationDiagnostics.Inspect(config,new("azure",Region:"eastasia ")).Checks.Any(c=>c.Field=="region"&&c.Level=="error"),"Azure region formatting has its own field hint");
Check(ApiConfigurationDiagnostics.Inspect(config,new("azure",Region:"")).Checks.Any(c=>c.Field=="region"&&c.Level=="warning"),"empty region is not automatically rejected for global single-service resources");

ApiFailureDiagnosis Error(HttpStatusCode status,string body="{}",string provider="llm")=>ApiConfigurationDiagnostics.HttpFailure(provider,status,body);
Check(Error(HttpStatusCode.Unauthorized).Field=="key","401 identifies authentication");
Check(Error(HttpStatusCode.PaymentRequired).Reason==LogReason.QuotaExceeded,"402 identifies balance/credit exhaustion");
Check(Error(HttpStatusCode.TooManyRequests,"{\"error\":{\"code\":\"insufficient_quota\"}}").Code=="quota","quota-limited 429 is distinguished from throughput rate limiting");
Check(Error(HttpStatusCode.TooManyRequests).Code=="rate-limit","plain 429 advises lowering request load");
Check(Error(HttpStatusCode.Forbidden,"{\"error\":{\"code\":403001}}","azure").Code=="quota","Azure exhausted free quota is not described as a wrong key");
Check(Error(HttpStatusCode.NotFound,"{\"error\":{\"code\":\"model_not_found\"}}").Field=="model","explicit model-not-found locates the model or its permission");
Check(Error(HttpStatusCode.NotFound).Code=="not-found"&&Error(HttpStatusCode.NotFound).Detail.Contains("暂不能"),"ambiguous 404 does not guess whether the route or model is wrong");
Check(Error(HttpStatusCode.Forbidden).Code=="permission","403 preserves permission ambiguity");
Check(Error(HttpStatusCode.ServiceUnavailable).Field=="service","503 does not falsely blame user input");
Check(Error(HttpStatusCode.Redirect).Code=="redirect","redirects advise correcting the endpoint without forwarding credentials");
Check(ApiConfigurationDiagnostics.NetworkFailure(new HttpRequestException(HttpRequestError.NameResolutionError,"secret-host",null)).Code=="dns","DNS errors have a separate address/network explanation");
Check(ApiConfigurationDiagnostics.NetworkFailure(new HttpRequestException(HttpRequestError.SecureConnectionError,"secret-host",null)).Code=="tls","TLS problems do not advise disabling certificate validation");

var settingsBefore=File.ReadAllBytes(Path.Combine(folder,"settings.json"));var secretBefore=File.ReadAllBytes(Path.Combine(folder,"secrets.dpapi"));
var response=HttpStatusCode.OK;string? errorBody=null;var calls=0;string? seenModel=null,seenKey=null;var mode="ok";
using var handler=new Fixture(async(req,ct)=>{
 calls++;seenKey=req.Headers.Authorization?.Parameter;
 var raw=await req.Content!.ReadAsStringAsync(ct);using var doc=JsonDocument.Parse(raw);if(doc.RootElement.ValueKind==JsonValueKind.Object)seenModel=doc.RootElement.GetProperty("model").GetString();
 if(mode=="html")return new(HttpStatusCode.OK){Content=new StringContent("<html>private-provider-page</html>")};
 return response==HttpStatusCode.OK?new(HttpStatusCode.OK){Content=JsonContent.Create(new{choices=new[]{new{finish_reason="stop",message=new{content="OK"}}}})}:new(response){Content=new StringContent(errorBody??"{}")};
});using var http=new HttpClient(handler);using var translators=new Translators(config,new Terms(folder,Path.Combine(folder,"missing-terms")),http);
await using var log=new DiagnosticLog(Path.Combine(folder,"probe-log"));
var blocked=await translators.TestApiConfiguration(new("llm",Model:""),log);Check(!blocked.Attempted&&calls==0,"local errors prevent network calls");
var success=await translators.TestApiConfiguration(new("llm",Model:"draft-model"),log);Check(success.Outcome=="ok"&&seenModel=="draft-model"&&seenKey=="fixture-private-key","unsaved draft model is tested with the selected existing key");
Check(settingsBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(folder,"settings.json")))&&secretBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(folder,"secrets.dpapi"))),"connection tests do not change saved configuration or DPAPI secrets");
response=HttpStatusCode.Unauthorized;errorBody="{\"error\":{\"message\":\"Invalid API key SECRET_ECHO\"}}";
var denied=await translators.TestApiConfiguration(new("llm"),log);Check(denied.Field=="key"&&denied.HttpStatus==401&&!JsonSerializer.Serialize(denied).Contains("SECRET_ECHO"),"provider body is classified without echoing its secret-bearing message");
response=HttpStatusCode.NotFound;errorBody="{\"error\":{\"code\":\"model_not_found\",\"message\":\"PRIVATE_MODEL_DETAILS\"}}";
var modelFailure=await translators.TestApiConfiguration(new("llm"),log);Check(modelFailure.Code=="model"&&translators.Issues.Length==0,"bad draft model does not pollute saved-runtime provider status");
mode="html";var html=await translators.TestApiConfiguration(new("llm"),log);Check(html.Code=="protocol"&&html.Field=="address","HTML success responses reveal wrong endpoint/protocol");
await log.FlushAsync();var events=string.Join('\n',(await log.SnapshotAsync()).Select(System.Text.Encoding.UTF8.GetString));Check(events.Contains("api-config-test")&&events.Contains("httpStatus\":401")&&!events.Contains("SECRET_ECHO")&&!events.Contains("fixture-private-key")&&!events.Contains("PRIVATE_MODEL_DETAILS")&&!events.Contains("api.example.test"),"API test logs preserve status/stage while excluding keys, addresses, models and response text");
var archive=await DiagnosticExport.Create(folder,log,new{phase="fixture"},config.Current);using(var zip=ZipFile.OpenRead(archive)){var exported=string.Join('\n',zip.Entries.Select(e=>{using var reader=new StreamReader(e.Open());return reader.ReadToEnd();}));Check(!exported.Contains("fixture-private-key")&&!exported.Contains("api.example.test")&&!exported.Contains("saved-model"),"export excludes private API settings and probe secrets");}
mode="ok";response=HttpStatusCode.NotFound;errorBody="{\"error\":{\"code\":\"model_not_found\"}}";
try{await translators.Quality("synthetic sentence",[],config.Current,CancellationToken.None);throw new Exception("Expected model failure");}catch(ProviderRequestException ex){Check(ex.Reason==LogReason.ModelUnavailable&&translators.Issues.Single().Code=="ModelUnavailable","normal translation also provides model-specific runtime diagnosis");}
var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var guardedCalls=0;
using(var guardedHttp=new HttpClient(new Fixture(async(_,ct)=>{guardedCalls++;entered.TrySetResult();await release.Task.WaitAsync(ct);return new(HttpStatusCode.OK){Content=JsonContent.Create(new{choices=new[]{new{message=new{content="OK"}}}})};})))using(var guarded=new Translators(config,new Terms(folder,Path.Combine(folder,"none")),guardedHttp)){
 var first=guarded.TestApiConfiguration(new("llm"));await entered.Task;var second=await guarded.TestApiConfiguration(new("llm"));Check(second.Outcome=="busy"&&!second.Attempted&&guardedCalls==1,"duplicate API probes do not make concurrent paid requests");release.TrySetResult();await first;
}
File.WriteAllText(Path.Combine(root,"artifacts","api-config-tests.json"),JsonSerializer.Serialize(new{passed,realApi=false,settingsPreserved=true}));Console.WriteLine("PASS TOTAL "+passed);

sealed class Fixture(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> action):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>action(request,token);}
