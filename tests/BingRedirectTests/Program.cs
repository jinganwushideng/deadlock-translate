using DeadlockTranslate;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

int passed=0;void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));var folder=Path.Combine(root,"artifacts","bing-redirect-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
ConfigStore Config(string name){var config=new ConfigStore(Path.Combine(folder,name));config.Save(config.Current with{FastProvider="bing",Mode="fast",FastConcurrency=4,OutgoingMode="fast",IncomingTargetLanguage="en"});return config;}
var terms=new Terms(folder,Path.Combine(root,"src/Bridge/data/terms.json"));
var source=new Uri("https://www.bing.com/translator");
Check(Translators.BingRedirect(source,new Uri("https://cn.bing.com/translator?market=zh-CN"),false,out var selected)&&selected!.Host=="cn.bing.com","official regional HTTPS page accepted");
Check(Translators.BingRedirect(source,new Uri("/translator?market=en",UriKind.Relative),false,out _),"relative official page redirect accepted");
foreach(var bad in new[]{"http://www.bing.com/translator","https://www.bing.com:444/translator","https://user:secret@www.bing.com/translator","https://www.bing.com/translator#fragment","https://www.bing.com.evil.test/translator","https://evil.test/translator","https://www.bing.com/challenge","https://login.live.com/translator","https://www.bing.com/translator/extra","https://127.0.0.1/translator"})Check(!Translators.BingRedirect(source,new Uri(bad),false,out _),"unsafe/nontranslation redirect blocked "+new Uri(bad).Host+new Uri(bad).AbsolutePath);
Check(!Translators.BingRedirect(source,new Uri("https://www.bing.com/ttranslatev3"),false,out _),"page credential request cannot be redirected to the POST endpoint");
Check(!Translators.IsRedirect(HttpStatusCode.UseProxy),"HTTP proxy instruction is not treated as localization");
static HttpResponseMessage Redirect(string url,HttpStatusCode code=HttpStatusCode.Redirect){var result=new HttpResponseMessage(code);result.Headers.Location=new Uri(url,UriKind.RelativeOrAbsolute);return result;}
static HttpResponseMessage Token(string id)=>new(HttpStatusCode.OK){Content=new StringContent("params_AbusePreventionHelper = [\"123\",\"token-"+id+"\"]; IG:\"ig-"+id+"\"")};
static HttpResponseMessage Translation()=>new(HttpStatusCode.OK){Content=JsonContent.Create(new[]{new{translations=new[]{new{text="fixture translated text"}}}})};
async Task<ProviderRequestException> Fail(Func<Task<string>> action){try{await action();}catch(ProviderRequestException ex){return ex;}throw new Exception("Expected structured provider failure");}

var config=Config("page");var posts=0;
using(var http=new HttpClient(new Mock(async(req,ct)=>{
 if(req.Method==HttpMethod.Get)return req.RequestUri!.Host=="www.bing.com"?Redirect("https://cn.bing.com/translator?market=zh"):Token("cn");
 posts++;Check(req.RequestUri!.Host=="cn.bing.com"&&req.Headers.Referrer!.Host=="cn.bing.com"&&req.Headers.GetValues("Origin").Single()=="https://cn.bing.com","POST origin and referrer follow the actual token page");
 Check((await req.Content!.ReadAsStringAsync(ct)).Contains("token-cn"),"regional page token is used at its own origin");return Translation();
})))using(var service=new Translators(config,terms,http)){
 Check(await service.Fast("synthetic regional sentence",config.Current,CancellationToken.None)=="fixture translated text"&&posts==1,"302 page localization succeeds without duplicate translation POSTs");
}

config=Config("post");var seenPosts=new List<(string Host,string Body)>();var gets=0;
using(var http=new HttpClient(new Mock(async(req,ct)=>{
 if(req.Method==HttpMethod.Get){gets++;return Token(req.RequestUri!.Host=="www.bing.com"?"www":"cn");}
 seenPosts.Add((req.RequestUri!.Host,await req.Content!.ReadAsStringAsync(ct)));
 return seenPosts.Count==1?Redirect("https://cn.bing.com/ttranslatev3?token=DO_NOT_FORWARD"):Translation();
})))using(var service=new Translators(config,terms,http)){
 await service.TranslateOutgoing("synthetic send message",config.Current,true,CancellationToken.None);
 Check(gets==2&&seenPosts.Count==2&&seenPosts[1].Host=="cn.bing.com","POST localization gets a fresh official page before one retry");
 Check(seenPosts[1].Body.Contains("token-cn")&&!seenPosts[1].Body.Contains("token-www")&&!seenPosts[1].Body.Contains("DO_NOT_FORWARD"),"old token and redirect query are never replayed at the new origin");
 Check(service.Issues.Length==0,"successfully handled localization does not raise a service fault");
}

config=Config("blocked");var requests=0;var recovered=false;
using(var http=new HttpClient(new Mock((req,ct)=>{requests++;return Task.FromResult(req.Method==HttpMethod.Get?Token("www"):recovered?Translation():Redirect("https://evil.test/login?text=PRIVATE_CHAT"));})))using(var service=new Translators(config,terms,http)){
 var failure=await Fail(()=>service.Fast("PRIVATE_CHAT",config.Current,CancellationToken.None));
 Check(requests==2&&failure.Reason==LogReason.ServiceRedirect&&failure.Stage=="bing-translate","untrusted POST redirect stops before contacting its target");
 Check(!failure.Message.Contains("PRIVATE_CHAT")&&!JsonSerializer.Serialize(service.Issues).Contains("PRIVATE_CHAT")&&!JsonSerializer.Serialize(service.Issues).Contains("evil.test"),"service diagnostics omit chat and redirect URLs");
 Check(service.Issues.Single().HttpStatus==302&&service.Issues.Single().Stage=="bing-translate","redirect status and request phase are retained");
 recovered=true;await service.Fast("recovery synthetic sentence",config.Current,CancellationToken.None);Check(service.Issues.Length==0,"successful network translation clears the previous provider warning");
}

config=Config("loop");requests=0;
using(var http=new HttpClient(new Mock((req,ct)=>{requests++;return Task.FromResult(Redirect("https://www.bing.com/translator?hop="+requests));})))using(var service=new Translators(config,terms,http)){
 var failure=await Fail(()=>service.Fast("bounded synthetic sentence",config.Current,CancellationToken.None));Check(requests==4&&failure.Stage=="bing-page","official page redirect chain is bounded to three hops");
}

config=Config("post-loop");posts=0;
using(var http=new HttpClient(new Mock((req,ct)=>{if(req.Method==HttpMethod.Get)return Task.FromResult(Token("www"));posts++;return Task.FromResult(Redirect("https://www.bing.com/ttranslatev3"));})))using(var service=new Translators(config,terms,http)){
 await Fail(()=>service.Fast("bounded post sentence",config.Current,CancellationToken.None));Check(posts==2,"translation POST refresh/retry is limited to once");
}

config=Config("cancel");var cancelMode=true;
using(var http=new HttpClient(new Mock(async(req,ct)=>{
 if(cancelMode){if(req.RequestUri!.Host=="www.bing.com")return Redirect("https://cn.bing.com/translator");await Task.Delay(Timeout.Infinite,ct);}
 return req.Method==HttpMethod.Get?Token("www"):Translation();
})))using(var service=new Translators(config,terms,http)){
 using var cancel=new CancellationTokenSource(60);try{await service.Fast("cancelled synthetic sentence",config.Current,cancel.Token);throw new Exception("Expected cancellation");}catch(OperationCanceledException){Check(true,"redirect credential requests honor the translation cancellation deadline");}
 cancelMode=false;await service.Fast("after cancelled sentence",config.Current,CancellationToken.None);Check(true,"cancellation releases credential and concurrency locks");
}

config=Config("llm");config.Save(config.Current with{LlmBaseUrl="https://llm.example.test/v1",LlmKey="fixture-key-only",Model="fixture-model"});requests=0;
using(var http=new HttpClient(new Mock((req,ct)=>{requests++;return Task.FromResult(Redirect("https://www.bing.com/translator"));})))using(var service=new Translators(config,terms,http)){
 var failure=await Fail(()=>service.Quality("LLM synthetic sentence",[],config.Current,CancellationToken.None));Check(requests==1&&failure.Provider=="llm"&&failure.Stage=="llm-translate","LLM bearer request does not share Bing's redirect exception");
 Check(failure.Message.Contains("BaseURL")&&!failure.Message.Contains("fixture-key"),"LLM redirect gives a safe endpoint correction hint");
}

config=Config("late");var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using(var http=new HttpClient(new Mock(async(req,ct)=>{
 if(req.Method==HttpMethod.Get)return Token("www");var body=await req.Content!.ReadAsStringAsync(ct);
 if(body.Contains("slow")){started.TrySetResult();await release.Task.WaitAsync(ct);return Translation();}
 return body.Contains("fail")?Redirect("https://evil.test/login"):Translation();
})))using(var service=new Translators(config,terms,http)){
 var older=service.Fast("slow synthetic sentence",config.Current,CancellationToken.None);await started.Task;
 await Fail(()=>service.Fast("fail synthetic sentence",config.Current,CancellationToken.None));release.TrySetResult();await older;
 Check(service.Issues.Length==1,"older in-flight success cannot hide a newer provider failure");
 await service.Fast("new healthy synthetic sentence",config.Current,CancellationToken.None);Check(service.Issues.Length==0,"a later successful probe clears the matching failure");
}

config=Config("audit");await using(var log=new DiagnosticLog(Path.Combine(folder,"audit-log")))using(var http=new HttpClient(new Mock((req,ct)=>Task.FromResult(req.Method==HttpMethod.Get?Token("www"):Redirect("https://evil.test/login?token=PRIVATE_TOKEN")))))using(var service=new Translators(config,terms,http))await using(var engine=new JobEngine(config,service,log)){
 engine.Exchange("private-session",[new("private-id","PRIVATE_CHAT")],[]);for(int i=0;i<100&&!engine.Exchange("private-session",[],["private-id"])[0].Done;i++)await Task.Delay(10);
 await log.FlushAsync();var logs=string.Join('\n',(await log.SnapshotAsync()).Select(System.Text.Encoding.UTF8.GetString));
 Check(logs.Contains("bing-translate")&&logs.Contains("httpStatus\":302")&&logs.Contains("ProviderRequestException"),"real job failure log preserves service, phase and HTTP status");
 Check(!logs.Contains("PRIVATE_CHAT")&&!logs.Contains("PRIVATE_TOKEN")&&!logs.Contains("evil.test")&&!logs.Contains("token-www"),"failure log contains no body, token or location header");
}
config=Config("epoch");started=new(TaskCreationOptions.RunContinuationsAsynchronously);release=new(TaskCreationOptions.RunContinuationsAsynchronously);
using(var http=new HttpClient(new Mock(async(req,ct)=>{if(req.Method==HttpMethod.Get)return Token("www");started.TrySetResult();await release.Task.WaitAsync(ct);return Redirect("https://evil.test/login");})))using(var service=new Translators(config,terms,http)){
 var stale=service.Fast("old settings synthetic sentence",config.Current,CancellationToken.None);await started.Task;config.Save(config.Current with{FastProvider="azure"});release.TrySetResult();await Fail(()=>stale);Check(service.Issues.Length==0,"old configuration responses cannot publish a new current-service warning");
}
config=Config("html-page");using(var http=new HttpClient(new Mock((req,ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("<html>verification</html>")}))))using(var service=new Translators(config,terms,http)){
 var failure=await Fail(()=>service.Fast("html page synthetic sentence",config.Current,CancellationToken.None));Check(failure.Reason==LogReason.InvalidData&&failure.Stage=="bing-page","a browser-readable page without credentials has a clear protocol/verification diagnosis");
}
config=Config("html-post");using(var http=new HttpClient(new Mock((req,ct)=>Task.FromResult(req.Method==HttpMethod.Get?Token("www"):new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("<html>verification</html>")}))))using(var service=new Translators(config,terms,http)){
 var failure=await Fail(()=>service.Fast("html post synthetic sentence",config.Current,CancellationToken.None));Check(failure.Reason==LogReason.InvalidData&&service.Issues.Single().Stage=="bing-translate","HTTP 200 HTML is not mistaken for a successful translation");
}
File.WriteAllText(Path.Combine(root,"artifacts","bing-redirect-tests.json"),JsonSerializer.Serialize(new{passed,realApi=false,redirectTargetsContacted=false}));Console.WriteLine("PASS TOTAL "+passed);
if(args.Contains("--public-bing")){
 var liveConfig=Config("public-smoke");using var handler=new HttpClientHandler{AllowAutoRedirect=false};using var http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(15)};using var live=new Translators(liveConfig,terms,http);using var token=new CancellationTokenSource(TimeSpan.FromSeconds(15));
 try{var result=await live.Fast("Public diagnostic translation check.",liveConfig.Current,token.Token);var ok=!string.IsNullOrWhiteSpace(result);File.WriteAllText(Path.Combine(root,"artifacts","bing-redirect-live-smoke.json"),JsonSerializer.Serialize(new{ok,syntheticTextOnly=true,paidApiUsed=false}));Console.WriteLine("PUBLIC BING "+(ok?"PASS":"EMPTY"));}
 catch(ProviderRequestException ex){File.WriteAllText(Path.Combine(root,"artifacts","bing-redirect-live-smoke.json"),JsonSerializer.Serialize(new{ok=false,httpStatus=(int?)ex.StatusCode,stage=ex.Stage,reason=ex.Reason.ToString(),syntheticTextOnly=true,paidApiUsed=false}));Console.WriteLine("PUBLIC BING "+(int?)ex.StatusCode+" "+ex.Stage);}
}

sealed class Mock(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> action):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>action(request,token);}
