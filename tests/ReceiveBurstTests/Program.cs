using DeadlockTranslate;
using System.Collections.Concurrent;
using System.Text.Json;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var folder=Path.Combine(root,"artifacts","receive-burst-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);int passed=0;
void Check(bool value,string name){if(!value)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
ConfigStore Config(string name,string mode="fast",int cap=4){var c=new ConfigStore(Path.Combine(folder,name));c.Save(c.Current with{Mode=mode,FastConcurrency=cap,LlmConcurrency=cap});return c;}
async Task Until(Func<bool> predicate){for(int i=0;i<600;i++){if(predicate())return;await Task.Delay(5);}throw new TimeoutException("condition not reached");}
bool Done(JobEngine e,string session,params string[] ids){var views=e.Exchange(session,[],ids);return views.Length==ids.Length&&views.All(v=>v.Done);}

var config=Config("separate-arrivals");var probe=new HeldTranslator();
await using(var engine=new JobEngine(config,probe)){
 engine.Exchange("match",[new("a","first")],[]);await Until(()=>probe.FastCalls==1);
 Check(engine.Scheduling is{Threshold:2,Unfinished:1,Burst:false,FastLimit:1},"first message starts immediately without waiting for another arrival");
 engine.Exchange("match",[new("b","second"),new("b","second")],[]);await Until(()=>probe.FastCalls==2);
 Check(probe.FastCalls==2&&engine.Scheduling is{Unfinished:2,Burst:true,FastLimit:4},"second later arrival starts before the held first result and retries do not inflate the count");
 engine.Exchange("match",[new("c","third")],[]);await Until(()=>probe.FastCalls==3);
 Check(probe.FastMax==3&&engine.Scheduling is{Unfinished:3,Burst:true,FastLimit:4},"further arrivals use the configured concurrent lanes");
 probe.Release(false,"third");await Until(()=>Done(engine,"match","c"));
 Check(engine.Scheduling is{Unfinished:2,Burst:true,FastLimit:4}&&probe.FastActive==2,"two remaining messages retain concurrent admission");
 probe.Release(false,"second");await Until(()=>Done(engine,"match","b"));
 Check(engine.Scheduling is{Unfinished:1,Burst:false,FastLimit:1}&&probe.FastActive==1,"single remaining message lowers future admission without cancelling work");
 engine.Exchange("match",[new("d","fourth")],[]);await Until(()=>probe.FastCalls==4);
 Check(engine.Scheduling.Burst&&engine.Scheduling.Unfinished==2,"another arrival can re-enable concurrency immediately");
 probe.ReleaseAll();await Until(()=>Done(engine,"match","a","b","c","d"));
 Check(engine.Scheduling is{Unfinished:0,Burst:false,FastLimit:1,QualityLimit:1},"drained queue resets to single admission");
}
config=Config("ordinary-pair");probe=new();
await using(var engine=new JobEngine(config,probe)){
 engine.Exchange("pair",[new("a","one"),new("b","two")],[]);await Until(()=>probe.FastCalls==2);
 Check(probe.FastActive==2&&probe.FastMax==2,"two messages in the same batch use separate lanes");
 probe.Release(false,"one");await Until(()=>probe.FastCalls==2);
 Check(probe.FastMax==2,"second message never needed a third arrival or the first completion");
 probe.ReleaseAll();await Until(()=>Done(engine,"pair","a","b"));
}
config=Config("context-only");probe=new();
await using(var engine=new JobEngine(config,probe)){
 engine.Exchange("ctx",[new("c1","context one",ContextOnly:true),new("c2","context two",ContextOnly:true),new("a","real")],[]);await Until(()=>probe.FastCalls==1);
 Check(engine.Scheduling is{Unfinished:1,Burst:false},"context-only records do not trigger concurrent translation");
 probe.ReleaseAll();await Until(()=>Done(engine,"ctx","a"));config.Save(config.Current with{Enabled=false});
 engine.Exchange("ctx",[new("x","disabled one"),new("y","disabled two"),new("z","disabled three")],[]);
 Check(engine.Scheduling.Unfinished==0&&probe.FastCalls==1,"disabled and skipped messages never inflate unfinished count");
}
config=Config("hybrid","hybrid");probe=new();
await using(var engine=new JobEngine(config,probe)){
 engine.Exchange("hybrid",[new("a","hybrid one"),new("b","hybrid two")],[]);await Until(()=>probe.FastCalls==2&&probe.QualityCalls==2);
 Check(engine.Scheduling.Unfinished==2&&engine.Scheduling.Burst&&probe.FastMax==2&&probe.QualityMax==2,"fast and quality stages do not double-count two logical messages");
 engine.Exchange("hybrid",[new("c","hybrid three")],[]);await Until(()=>probe.FastCalls==3&&probe.QualityCalls==3);
 Check(engine.Scheduling.Burst&&probe.FastMax==3&&probe.QualityMax==3,"both provider channels remain independently concurrent");
 foreach(var text in new[]{"hybrid one","hybrid two","hybrid three"})probe.Release(false,text);
 await Until(()=>probe.FastActive==0);
 Check(engine.Scheduling.Unfinished==3&&engine.Scheduling.Burst,"fast completion alone does not count a pending quality message as finished");
 probe.ReleaseAll();await Until(()=>Done(engine,"hybrid","a","b","c"));
 Check(engine.Exchange("hybrid",[],["a","b","c"]).All(x=>x.Stage=="quality")&&engine.Scheduling.Unfinished==0,"quality precedence and one decrement per logical message are preserved");
}
config=Config("caps","fast",2);probe=new();
await using(var engine=new JobEngine(config,probe)){
 engine.Exchange("caps",[new("a","cap one"),new("b","cap two"),new("c","cap three")],[]);await Until(()=>probe.FastCalls==2);await Task.Delay(40);
 Check(probe.FastMax==2&&engine.Scheduling.FastLimit==2,"burst never overrides configured concurrency cap");
 config.Save(config.Current with{FastConcurrency=4});await Until(()=>probe.FastCalls==3);
 Check(engine.Scheduling.FastLimit==4,"raising the cap wakes already waiting burst work");
 config.Save(config.Current with{FastConcurrency=1});engine.Exchange("caps",[new("d","cap four")],[]);await Task.Delay(40);
 Check(probe.FastCalls==3&&probe.Cancelled==0&&engine.Scheduling.FastLimit==1,"lowering cap does not cancel active work or start an extra request");
 probe.ReleaseAll();await Until(()=>Done(engine,"caps","a","b","c","d"));
}
config=Config("rollover");probe=new();
await using(var engine=new JobEngine(config,probe)){
 engine.Exchange("old",[new("a","old one"),new("b","old two"),new("c","old three")],[]);await Until(()=>probe.FastCalls==3);
 engine.Exchange("new",[new("n","fresh")],[]);await Until(()=>probe.FastCalls==4);
 Check(engine.Scheduling is{Unfinished:1,Burst:false,FastLimit:1},"match rollover discards old unfinished count and starts the new scene serially");
 engine.Exchange("old",[new("stale","must not run")],[]);await Task.Delay(25);
 Check(engine.IsCurrentSession("new")&&engine.Scheduling.Unfinished==1&&probe.FastCalls==4,"late retired scene cannot change current burst state");
 probe.ReleaseAll();await Until(()=>Done(engine,"new","n"));
}
config=Config("quality","quality");probe=new();
await using(var engine=new JobEngine(config,probe)){
 engine.Exchange("q",[new("a","quality one"),new("b","quality two"),new("c","quality three")],[]);await Until(()=>probe.QualityCalls==3);
 Check(probe.FastCalls==0&&probe.QualityMax==3,"quality-only mode uses the configured limit");
 probe.ReleaseAll();await Until(()=>Done(engine,"q","a","b","c"));
 engine.Exchange("q",[new("fail","fail")],[]);await Until(()=>Done(engine,"q","fail"));
 Check(engine.Scheduling.Unfinished==0&&!engine.Scheduling.Burst,"provider errors release unfinished count exactly once");
}
config=Config("noncooperative","fast",8);probe=new(){IgnoreCancellation=true};
await using(var engine=new JobEngine(config,probe)){
 engine.Exchange("old",Enumerable.Range(0,8).Select(i=>new Incoming("old"+i,"uncooperative "+i)).ToArray(),[]);await Until(()=>probe.FastCalls==8);
 engine.Exchange("new",[new("fresh","fresh scene")],[]);await Until(()=>probe.FastCalls==9);
 Check(engine.Scheduling is{Unfinished:1,Burst:false,FastLimit:1},"eight old providers ignoring cancellation cannot occupy the new scene's single lane");
 probe.ReleaseAll();await Until(()=>Done(engine,"new","fresh"));await Task.Delay(25);
 Check(engine.Scheduling.Unfinished==0&&engine.Exchange("new",[],["fresh"])[0].Text=="fast fresh scene","late old completions cannot decrement or replace the fresh scene's result");
}
File.WriteAllText(Path.Combine(root,"artifacts/receive-burst-tests.json"),JsonSerializer.Serialize(new{passed,threshold=2,realGame=false,cloudUsed=false}));Console.WriteLine("TOTAL "+passed+" passed");
sealed class HeldTranslator:ITranslator{
 private readonly object gate=new();private readonly ConcurrentDictionary<(bool,string),TaskCompletionSource> releases=new();private bool allReleased;
 public int FastCalls,QualityCalls,FastActive,QualityActive,FastMax,QualityMax,Cancelled;
 public bool IgnoreCancellation;
 public void Release(bool quality,string text)=>releases.GetOrAdd((quality,text),_=>new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
 public void ReleaseAll(){lock(gate){allReleased=true;foreach(var release in releases.Values)release.TrySetResult();}}
 public Task<string> Fast(string text,Settings c,CancellationToken ct)=>Run(text,false,ct);
 public Task<string> Quality(string text,string[] context,Settings c,CancellationToken ct)=>Run(text,true,ct);
 private async Task<string> Run(string text,bool quality,CancellationToken ct){
  var release=releases.GetOrAdd((quality,text),_=>new(TaskCreationOptions.RunContinuationsAsynchronously));
  lock(gate){if(quality){QualityCalls++;QualityActive++;QualityMax=Math.Max(QualityMax,QualityActive);}else{FastCalls++;FastActive++;FastMax=Math.Max(FastMax,FastActive);}if(allReleased)release.TrySetResult();}
  try{if(text=="fail")throw new IOException("fixture failure");if(IgnoreCancellation)await release.Task;else await release.Task.WaitAsync(ct);return (quality?"quality ":"fast ")+text;}
  catch(OperationCanceledException){Interlocked.Increment(ref Cancelled);throw;}
  finally{lock(gate){if(quality)QualityActive--;else FastActive--;}}
 }
}
