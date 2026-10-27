using DeadlockTranslate;
using System.Text.Json;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var temp=Path.Combine(root,"artifacts","chat-pipeline-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
var config=new ConfigStore(temp);config.Save(config.Current with{Mode="fast",FastConcurrency=4});
var probe=new Probe();int passed=0;
void Check(bool value,string name){if(!value)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
async Task<JobView> Done(JobEngine engine,string session,string id){for(int n=0;n<500;n++){var result=engine.Exchange(session,[],[id]);if(result.Length==1&&result[0].Done)return result[0];await Task.Delay(5);}throw new TimeoutException(id);}
await using(var engine=new JobEngine(config,probe)){
 engine.Exchange("old",[new("same-id","old message")],[]);await probe.OldStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
 engine.Exchange("current",[new("same-id","current message")],[]);
 Check((await Done(engine,"current","same-id")).Text=="translated current message","new session can reuse an old message ID without inheriting its result");
 var calls=Volatile.Read(ref probe.Calls);var stale=engine.Exchange("old",[new("stale","should never translate")],["same-id"]);
 Check(stale.Length==0,"a retired session produces no stale results");
 Check(engine.IsCurrentSession("current"),"late old request does not roll back the current session");
 await Task.Delay(30);Check(Volatile.Read(ref probe.Calls)==calls,"late old request does not invoke the provider");
 Check((await Done(engine,"current","same-id")).Text=="translated current message","late old request leaves the current job intact");
 probe.ReleaseOld.TrySetResult();await Task.Delay(30);
 Check((await Done(engine,"current","same-id")).Text=="translated current message","provider completion after cancellation cannot contaminate a reused ID");
 for(var i=0;i<80;i++)engine.Exchange("scene-"+i,[],[]);
 Check(engine.Exchange("scene-78",[new("stale","not requested")],[]).Length==0&&engine.IsCurrentSession("scene-79"),"bounded retirement still rejects recent old owners after many transitions");
 var contexts=engine.OutgoingContext("scene-79","team",config.Current with{OutgoingUseContext=true});Check(contexts.Length==0,"new scene has no previous chat context");
}
var metrics=new Metrics(1,1,1,1,1,0,1,HudContainers:8);
Check(GameMetricSummary.From(metrics).Clean().HudContainers==8,"diagnostics retain simultaneous current and retired HUD candidate counts");
File.WriteAllText(Path.Combine(root,"artifacts/chat-pipeline-backend-tests.json"),JsonSerializer.Serialize(new{passed,realGame=false,cloudUsed=false}));Console.WriteLine("TOTAL "+passed+" passed");
sealed class Probe:ITranslator{
 public int Calls;public TaskCompletionSource OldStarted=new(TaskCreationOptions.RunContinuationsAsynchronously),ReleaseOld=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public async Task<string> Fast(string text,Settings config,CancellationToken ct){Interlocked.Increment(ref Calls);if(text=="old message"){OldStarted.TrySetResult();await ReleaseOld.Task;}return "translated "+text;}
 public Task<string> Quality(string text,string[] context,Settings config,CancellationToken ct)=>Fast(text,config,ct);
}
