using DeadlockTranslate;
using System.Net;
using System.Text;
using System.Text.Json;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));var directory=Path.Combine(root,"artifacts","keepalive-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
var passed=0;void Check(bool value,string name){if(!value)throw new Exception("FAIL "+name);passed++;Console.WriteLine("PASS "+name);}
Check(!new Settings().LlmKeepAliveEnabled&&new Settings().LlmKeepAliveSeconds==120,"existing and fresh configurations default to no paid maintenance requests");
foreach(var seconds in new[]{59,1801}){var rejected=false;try{ConfigStore.Validate(new Settings{LlmKeepAliveSeconds=seconds});}catch(ArgumentException){rejected=true;}Check(rejected,"interval rejected outside limits "+seconds);}
var store=new ConfigStore(Path.Combine(directory,"state"));var s=new Settings{LlmBaseUrl="http://localhost:19010/v1",Model="private-model-name",LlmKey="fixture-key",Mode="quality",OutgoingMode="quality",LlmConcurrency=1,LlmThinkingApi="deepseek",LlmThinkingEnabled=true,LlmStreaming=false};store.Save(s);
long now=100000;var connected=false;var fake=new Probe();
await using(var maintenance=new LlmKeepAlive(store,fake,()=>connected,start:false,clock:()=>now)){
 await maintenance.Tick();Check(fake.Calls==0,"automatic requests stay disabled");
 await maintenance.WarmOnce();Check(fake.Calls==1,"explicit one-time warmup works without enabling the timer or starting a game");
 Check(!store.Current.LlmKeepAliveEnabled,"one-time request cannot silently enable recurring charges");
 await maintenance.WarmOnce();Check(fake.Calls==1,"rapid clicks respect minimum request spacing");
 now+=61000;store.Save(s with{LlmKeepAliveEnabled=true});await maintenance.Tick();Check(fake.Calls==1&&maintenance.Status.State=="waiting-for-game","automatic requests pause without game connection");
 connected=true;await maintenance.Tick();Check(fake.Calls==2&&maintenance.Status.Models==1,"same receive and send model share one maintenance request");
 now+=125000;fake.LastUse=now-10000;await maintenance.Tick();Check(fake.Calls==2,"real model activity postpones maintenance without another HTTP request");
 fake.LastUse=0;await maintenance.Tick();Check(fake.Calls==3,"idle model becomes eligible after configured interval");
 now+=125000;fake.Outcome=new("rate-limited",LogReason.RateLimit,429,600,true,7);await maintenance.Tick();Check(fake.Calls==4&&maintenance.Status.HttpStatus==429,"rate limit is isolated in maintenance status");
 now+=300000;await maintenance.Tick();Check(fake.Calls==4,"rate-limit retry-after prevents repeated paid attempts");
 now+=301000;fake.Outcome=new("unauthorized",LogReason.Unauthorized,401,Attempted:true);await maintenance.Tick();Check(fake.Calls==5,"failed credential check is surfaced once");
 now+=1900000;await maintenance.Tick();Check(fake.Calls==5,"bad credentials do not trigger an endless retry loop");
 store.Save(store.Current with{LlmKeepAliveSeconds=300});fake.Outcome=new("succeeded",HttpStatus:200,Attempted:true);await maintenance.Tick();Check(fake.Calls==6,"saving corrected configuration permits a new attempt");
}
now+=100000;fake=new Probe();store.Save(s with{LlmKeepAliveEnabled=true,LlmIndependentProfiles=true,IncomingModel="receive-private",OutgoingModel="send-private"});
await using(var maintenance=new LlmKeepAlive(store,fake,()=>true,start:false,clock:()=>now)){await maintenance.Tick();Check(fake.Calls==2&&maintenance.Status.Models==2,"distinct active models warm separately");}
fake=new Probe{HasKeepAliveCredentials=false};await using(var maintenance=new LlmKeepAlive(store,fake,()=>true,start:false,clock:()=>now)){await maintenance.WarmOnce();Check(fake.Calls==0&&maintenance.Status.State=="unconfigured","missing key never sends an unauthenticated warmup");}
var slots=new PriorityConcurrency(1,true);using(var active=await slots.Enter(CancellationToken.None)){Check(slots.TryEnterIdle()==null,"maintenance cannot enter behind active translation");}using(var idle=slots.TryEnterIdle()){Check(idle!=null,"idle maintenance lease can be admitted");}Check(((JsonElement)JsonSerializer.SerializeToElement(slots.Stats)).GetProperty("active").GetInt32()==0,"idle lease returns provider capacity");

store.Save(s);var handler=new WarmHandler();using var http=new HttpClient(handler);var terms=new Terms(store.DirectoryPath,Path.Combine(root,"src/Bridge/data/terms.json"));
await using var log=new DiagnosticLog(Path.Combine(directory,"logs"));using(var translator=new Translators(store,terms,http,log)){
 var warm=translator.TryKeepAlive(store.Current,store.Current.Model,CancellationToken.None);await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
 Check((await translator.TryKeepAlive(store.Current,store.Current.Model,CancellationToken.None)).State=="busy","simultaneous warmups never create multiple low-priority requests");
 var real=translator.TranslateOutgoing("这是保活抢占验证的一句话。",store.Current,false,CancellationToken.None);
 Check(await real.WaitAsync(TimeSpan.FromSeconds(3))=="fixture ready","real outgoing translation preempts warmup with only one provider slot");
 Check((await warm).State=="interrupted"&&handler.Cancelled,"warmup HTTP is cancelled instead of blocking real translation");
 Check(handler.WarmBody.GetProperty("max_tokens").GetInt32()==16&&handler.WarmBody.GetProperty("thinking").GetProperty("type").GetString()=="disabled","warmup uses a tiny independent non-thinking request");
 Check(handler.RealBody.GetProperty("max_tokens").GetInt32()==4096&&handler.RealBody.GetProperty("thinking").GetProperty("type").GetString()=="enabled","translation keeps the user's thinking and output budget");
 Check(!handler.WarmBody.ToString().Contains("保活抢占")&&!handler.WarmBody.ToString().Contains("fixture-key"),"warmup never contains chat context or API key in its body");
 Check(translator.Telemetry.Recent.Length==1,"maintenance does not pollute real translation timing history");
}
fake=new Probe();store.Save(s with{LlmKeepAliveEnabled=true});await using(var maintenance=new LlmKeepAlive(store,fake,()=>true,log,start:false,clock:()=>now)){await maintenance.Tick();}
await log.FlushAsync();var text=string.Join("",(await log.SnapshotAsync()).Select(Encoding.UTF8.GetString));Check(text.Contains("LlmKeepAlive")&&!text.Contains("private-model-name")&&!text.Contains("fixture-key")&&!text.Contains("保活抢占"),"maintenance diagnostics contain only bounded metadata");
File.WriteAllText(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new{passed,cloudUsed=false,gameTested=false}));Console.WriteLine("TOTAL "+passed+" passed");

sealed class Probe:ILlmKeepAliveProvider {
 public bool HasKeepAliveCredentials {get;set;}=true;public int Calls;public long LastUse;public KeepAliveOutcome Outcome=new("succeeded",HttpStatus:200,Attempted:true,ElapsedMs:4);
 public long LastModelActivity(string model)=>LastUse;
 public Task<KeepAliveOutcome> TryKeepAlive(Settings s,string model,CancellationToken token){Calls++;return Task.FromResult(Outcome);}
 public void CancelKeepAlive(){}
}
sealed class WarmHandler:HttpMessageHandler {
 public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);public bool Cancelled;public JsonElement WarmBody,RealBody;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
  var body=JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
  if(body.GetProperty("messages")[0].GetProperty("content").GetString()=="Reply only OK."){
   WarmBody=body;Started.TrySetResult();try{await Task.Delay(Timeout.Infinite,token);}catch(OperationCanceledException){Cancelled=true;throw;}
  }else RealBody=body;
  return new(HttpStatusCode.OK){Content=new StringContent("{\"choices\":[{\"message\":{\"content\":\"fixture ready\"},\"finish_reason\":\"stop\"}]}",Encoding.UTF8,"application/json")};
 }
}
