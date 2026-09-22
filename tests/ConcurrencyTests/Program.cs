using DeadlockTranslate;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var folder=Path.Combine(root,"artifacts/concurrency",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
int passed=0;
void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);passed++;Console.WriteLine("PASS "+name);}
ConfigStore Config(string name,int llm=4,int fast=4)=>MakeConfig(Path.Combine(folder,name),llm,fast);

var gate=new AsyncConcurrency(1);using var first=await gate.Enter(CancellationToken.None);
using var cancelled=new CancellationTokenSource();var abandoned=gate.Enter(cancelled.Token);cancelled.Cancel();
try{await abandoned;throw new Exception("cancelled waiter was admitted");}catch(OperationCanceledException){}
var second=gate.Enter(CancellationToken.None);gate.SetLimit(2);using var secondLease=await second.WaitAsync(TimeSpan.FromSeconds(2));
gate.SetLimit(1);var third=gate.Enter(CancellationToken.None);secondLease.Dispose();
Check(!third.IsCompleted,"reducing a limit waits for already running work to drain");
first.Dispose();using var thirdLease=await third.WaitAsync(TimeSpan.FromSeconds(2));
Check(true,"cancelled waiters do not leak permits; increases wake pending work");

var config=Config("incoming");var incoming=new ProbeTranslator();await using(var engine=new JobEngine(config,incoming)){
 var input=Enumerable.Range(0,12).Select(i=>new Incoming("m"+i,i==0?"slow":"message "+i,"","all")).ToArray();
 engine.Exchange("match",input,[]);
 await Until(()=>incoming.FastMax>=4&&incoming.QualityMax>=4);
 await Until(()=>engine.Exchange("match",[],["m1"]).Any(x=>x.Done));
 Check(!engine.Exchange("match",[],["m0"])[0].Done,"one slow quality response does not block other messages");
 incoming.Release.TrySetResult();
 await Until(()=>engine.Exchange("match",[],input.Select(x=>x.Id).ToArray()).All(x=>x.Done));
 Check(engine.Exchange("match",[],input.Select(x=>x.Id).ToArray()).All(x=>x.Stage=="quality"),"hybrid results retain per-message quality precedence under a burst");
 Check(incoming.FastMax==4&&incoming.QualityMax==4,"incoming fast and quality workers obey independent configured limits");
}

var terms=new Terms(folder,Path.Combine(root,"src/Bridge/data/terms.json"));
var sceneConfig=Config("scene");sceneConfig.Save(sceneConfig.Current with{Mode="fast",FastConcurrency=1,FastTimeoutSeconds=15});
var sceneProbe=new SceneProbe();await using(var engine=new JobEngine(sceneConfig,sceneProbe)){
 engine.Exchange("old",Enumerable.Range(0,16).Select(i=>new Incoming("old"+i,"old message")).ToArray(),[]);
 await sceneProbe.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
 for(int batch=1;batch<6;batch++)engine.Exchange("old",Enumerable.Range(batch*16,16).Select(i=>new Incoming("old"+i,"old message")).ToArray(),[]);
 engine.Exchange("new",[new Incoming("new","new message")],[]);
 await Until(()=>engine.Exchange("new",[],["new"]).Any(x=>x.Done));
 Check(engine.Exchange("new",[],["new"])[0].Text=="fresh scene","a full previous-match queue cannot reject the first new-match message");
}
var httpConfig=Config("shared",3,4);var network=new ProbeHttp{Hold=new(TaskCreationOptions.RunContinuationsAsynchronously)};
using(var http=new HttpClient(network))using(var service=new Translators(httpConfig,terms,http)){
 var tasks=Enumerable.Range(0,8).Select(i=>service.Quality("synthetic sentence "+i,[],httpConfig.Current,CancellationToken.None))
  .Concat(Enumerable.Range(0,4).Select(i=>service.ToEnglish("测试消息"+i,httpConfig.Current,false,CancellationToken.None))).ToArray();
 await Until(()=>network.Max==3);await Task.Delay(40);
 Check(network.Active==3,"received and outgoing LLM requests share one global budget");
 httpConfig.Save(httpConfig.Current with{LlmConcurrency=5});await Until(()=>network.Active==5);
 Check(network.Max==5,"changing concurrency immediately admits waiting work");
 httpConfig.Save(httpConfig.Current with{LlmConcurrency=2});network.Hold.TrySetResult();await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
 Check(network.Calls==12,"a concurrency change leaves no messages stranded");
 var stale=httpConfig.Current;httpConfig.Save(stale with{LlmBaseUrl="https://changed.example/v1",LlmKey="different-fake-key"});var before=network.Calls;
 try{await service.Quality("another synthetic sentence",[],stale,CancellationToken.None);throw new Exception("stale provider snapshot accepted");}catch(InvalidOperationException){}
 Check(network.Calls==before,"queued old endpoints never receive credentials from a new configuration");
}

var bingConfig=Config("bing");var bingNetwork=new ProbeHttp();
using(var http=new HttpClient(bingNetwork))using(var service=new Translators(bingConfig,terms,http)){
 var tasks=Enumerable.Range(0,6).Select(i=>service.Fast("synthetic sentence "+i,bingConfig.Current,CancellationToken.None))
  .Concat(Enumerable.Range(0,2).Select(i=>service.ToEnglish("测试消息"+i,bingConfig.Current,true,CancellationToken.None))).ToArray();
 await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
 Check(bingNetwork.TokenCalls==1,"simultaneous Bing startup refreshes credentials once");
 Check(bingNetwork.Max==4&&bingNetwork.Calls==8,"Bing translation POSTs run concurrently across receive and send");
}

var limitedConfig=Config("limited");var limitedNetwork=new ProbeHttp{RateLimit=true};
using(var http=new HttpClient(limitedNetwork))using(var service=new Translators(limitedConfig,terms,http)){
 try{await service.Fast("synthetic first",limitedConfig.Current,CancellationToken.None);throw new Exception("expected 429");}catch(HttpRequestException e) when(e.StatusCode==HttpStatusCode.TooManyRequests){}
 try{await service.ToEnglish("测试第二句",limitedConfig.Current,true,CancellationToken.None);throw new Exception("expected cooldown");}catch(InvalidOperationException){}
 Check(limitedNetwork.Calls==1,"a 429 prevents new requests from the other queue during Retry-After");
}

var renewConfig=Config("renew");var renewal=new RenewalHttp();
using(var http=new HttpClient(renewal))using(var service=new Translators(renewConfig,terms,http)){
 var old=service.Fast("old-slow",renewConfig.Current,CancellationToken.None);await renewal.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
 try{await service.Fast("old-fast",renewConfig.Current,CancellationToken.None);throw new Exception("expected invalid token");}catch(HttpRequestException){}
 await service.Fast("fresh",renewConfig.Current,CancellationToken.None);
 renewal.Release.TrySetResult();try{await old;throw new Exception("expected late failure");}catch(HttpRequestException){}
 await service.Fast("fresh-again",renewConfig.Current,CancellationToken.None);
 Check(renewal.Gets==2,"an old parallel Bing failure cannot invalidate newly refreshed credentials");
}

var sendingConfig=Config("sending");var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int active=0,max=0;
await using(var outgoing=new OutgoingService(sendingConfig,async(text,settings,fallback,token)=>{
 var n=Interlocked.Increment(ref active);Max(ref max,n);
 try{if(text=="first")await release.Task.WaitAsync(token);else await Task.Delay(40,token);return "translated "+text;}finally{Interlocked.Decrement(ref active);}
})){
 outgoing.Exchange("s",[new("1","first","team"),new("2","second","team"),new("3","other","all")],[]);
 await Until(()=>outgoing.Recent.Any(x=>x.Id=="2"&&x.State=="waiting_order"));
 Check(max==3,"outgoing translation tasks overlap instead of running one by one");
 Check(outgoing.Recent.Single(x=>x.Id=="3").State=="ready","a slow message in one channel does not block another channel");
 release.TrySetResult();await Until(()=>outgoing.Recent.Single(x=>x.Id=="1").State=="ready");
 Check(outgoing.Recent.Single(x=>x.Id=="2").State=="waiting_order","fast later translations cannot overtake the first message in a channel");
 outgoing.Exchange("s",[],["1","3"]);Check(outgoing.Recent.Single(x=>x.Id=="2").State=="ready","acknowledgement releases the next translated message");
 outgoing.Exchange("s",[],["2"]);
 for(int batch=0;batch<4;batch++)outgoing.Exchange("s",Enumerable.Range(batch*3,3).Select(i=>new OutgoingInput("b"+i,"burst "+i,"team")).ToArray(),[]);
 var overflow=outgoing.Exchange("s",[new("overflow","keep original","team")],[]).Single(x=>x.Id=="overflow");
 Check(overflow.State=="failed"&&overflow.Original=="keep original","queue capacity is bounded and rejected text is retained");
 await Until(()=>outgoing.Recent.Count(x=>x.Id.StartsWith("b")&&x.English.Length>0)==12);
 Check(true,"all twelve accepted burst messages complete translation");
}

async Task<double> Benchmark(int concurrency){
 var c=Config("bench"+concurrency,concurrency);using var http=new HttpClient(new ProbeHttp{DelayMs=120});using var service=new Translators(c,terms,http);
 var timer=Stopwatch.StartNew();await Task.WhenAll(Enumerable.Range(0,12).Select(i=>service.Quality("benchmark sentence "+i,[],c.Current,CancellationToken.None)));return timer.Elapsed.TotalMilliseconds;
}
var serialMs=await Benchmark(1);var parallelMs=await Benchmark(4);
Check(parallelMs<serialMs*.7,"four-way service concurrency shortens controlled twelve-message completion time");
ConfigStore.Validate(config.Current with{LlmConcurrency=8,FastConcurrency=8,OutgoingConcurrency=8,OutgoingQueueLimit=32});
Check(true,"maximum configured concurrency and capacity are accepted");
bool invalid=false;try{ConfigStore.Validate(config.Current with{FastConcurrency=9});}catch(ArgumentException){invalid=true;}
Check(invalid,"concurrency above the hard bound is rejected");
File.WriteAllText(Path.Combine(root,"artifacts/concurrency-tests.json"),JsonSerializer.Serialize(new{passed,serialMs,parallelMs,at=DateTime.UtcNow,scope="local delayed HTTP handler only; no real API quota, game sends or FPS measurement"},ConfigStore.Json));
Console.WriteLine(JsonSerializer.Serialize(new{passed,serialMs,parallelMs}));

static ConfigStore MakeConfig(string path,int llm,int fast){var c=new ConfigStore(path);c.Save(c.Current with{LlmConcurrency=llm,FastConcurrency=fast,OutgoingConcurrency=3,OutgoingQueueLimit=12,LlmKey="fake-key",LlmBaseUrl="https://local-fixture.example/v1"});return c;}
static async Task Until(Func<bool> predicate){for(int i=0;i<600;i++){if(predicate())return;await Task.Delay(5);}throw new TimeoutException("condition not reached");}
static void Max(ref int field,int value){int old;do{old=Volatile.Read(ref field);if(old>=value)return;}while(Interlocked.CompareExchange(ref field,value,old)!=old);}
sealed class ProbeTranslator:ITranslator{
 public int FastActive,FastMax,QualityActive,QualityMax;public TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
 private readonly object gate=new();
 public async Task<string> Fast(string text,Settings c,CancellationToken ct){lock(gate){FastActive++;FastMax=Math.Max(FastMax,FastActive);}try{await Task.Delay(70,ct);return "fast "+text;}finally{lock(gate)FastActive--;}}
 public async Task<string> Quality(string text,string[] context,Settings c,CancellationToken ct){lock(gate){QualityActive++;QualityMax=Math.Max(QualityMax,QualityActive);}try{if(text=="slow")await Release.Task.WaitAsync(ct);else await Task.Delay(100,ct);return "quality "+text;}finally{lock(gate)QualityActive--;}}
}
sealed class SceneProbe:ITranslator{
 public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public async Task<string> Fast(string text,Settings c,CancellationToken ct){if(text=="new message")return "fresh scene";Started.TrySetResult();await Task.Delay(Timeout.Infinite,ct);return "old";}
 public Task<string> Quality(string text,string[] context,Settings c,CancellationToken ct)=>Fast(text,c,ct);
}
sealed class ProbeHttp:HttpMessageHandler{
 public int Calls,TokenCalls,Active,Max;public int DelayMs=80;public bool RateLimit;public TaskCompletionSource? Hold;private readonly object gate=new();
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
  if(request.Method==HttpMethod.Get){Interlocked.Increment(ref TokenCalls);await Task.Delay(40,token);return new(HttpStatusCode.OK){Content=new StringContent("params_AbusePreventionHelper = [\"123\",\"fake-token\"]; IG:\"fake-ig\"")};}
  Interlocked.Increment(ref Calls);lock(gate){Active++;Max=Math.Max(Max,Active);}
  try{
   if(RateLimit){var limited=new HttpResponseMessage(HttpStatusCode.TooManyRequests);limited.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));return limited;}
   if(Hold!=null)await Hold.Task.WaitAsync(token);await Task.Delay(DelayMs,token);
   var body=request.RequestUri!.Host=="www.bing.com"?"[{\"translations\":[{\"text\":\"fixture translation\"}]}]":"{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"fixture translation\"}}]}";
   return new(HttpStatusCode.OK){Content=new StringContent(body)};
  }finally{lock(gate)Active--;}
 }
}
sealed class RenewalHttp:HttpMessageHandler{
 public int Gets;public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
  if(request.Method==HttpMethod.Get){var n=Interlocked.Increment(ref Gets);return new(HttpStatusCode.OK){Content=new StringContent("params_AbusePreventionHelper = [\"123\",\"token"+n+"\"]; IG:\"ig\"")};}
  var form=await request.Content!.ReadAsStringAsync(token);
  if(form.Contains("old-slow")){Started.TrySetResult();await Release.Task.WaitAsync(token);return new(HttpStatusCode.Unauthorized);}
  if(form.Contains("old-fast"))return new(HttpStatusCode.Unauthorized);
  return new(HttpStatusCode.OK){Content=new StringContent("[{\"translations\":[{\"text\":\"fresh result\"}]}]")};
 }
}
