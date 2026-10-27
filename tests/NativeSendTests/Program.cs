using DeadlockTranslate;
using System.Text;
using System.Text.Json;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var folder=Path.Combine(root,"artifacts/send-native-20261027/service-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
var config=new ConfigStore(folder);config.Save(config.Current with{OutgoingTimeoutSeconds=30,OutgoingSeparateBilingual=true,OutgoingSplitLong=true,OutgoingQueueLimit=12});
await using var log=new DiagnosticLog(folder);
long now=1000;var calls=0;await using var sender=new OutgoingService(config,(text,c,fast,ct)=>{Interlocked.Increment(ref calls);return Task.FromResult(text=="LONG"?new string('x',180):"translated");},()=>now,log:log);
var results=new List<object>();
var visibility=new List<OutgoingView>();
void Check(bool value,string name){results.Add(new{name,ok=value});if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);}
OutgoingView View(string id)=>sender.Recent.Single(x=>x.Id==id);
async Task Ready(string id){for(var i=0;i<200;i++){if(View(id).State=="ready")return;await Task.Delay(5);}throw new TimeoutException(id);}
OutgoingStatus Status(long seq,string id,int part,string state,string? reason=null,string channel="team")=>new(seq,id,part,state,channel,now,reason);
void Exchange(OutgoingStatus[] status,OutgoingPartAck[]? ack=null,string[]? sent=null)=>sender.Exchange("session",[],sent??[],partAcks:ack,statuses:status);

var beforeHeld=calls;sender.Exchange("session",[new("held-native","可找回的首条原文","team","bilingual",HoldForInput:true)],[]);await Task.Delay(40);
Check(View("held-native").State=="waiting_input"&&View("held-native").Original=="可找回的首条原文"&&calls==beforeHeld,"registered native source is visible and retained without translation before close confirmation");visibility.Add(View("held-native"));
sender.Exchange("session",[],["held-native"],partAcks:[new("held-native",0)]);Check(View("held-native").State=="waiting_input"&&calls==beforeHeld,"premature dispatch ACK cannot bypass the native-close registration gate");
Exchange([Status(1,"held-native",0,"waiting_input","settings")]);Check(View("held-native").WaitReason=="settings"&&calls==beforeHeld,"registered source reports settings wait without a cloud request");
Exchange([Status(2,"held-native",0,"capture_ready")]);await Ready("held-native");Check(calls==beforeHeld+1,"stable-close release starts translation exactly once");
Exchange([Status(2,"held-native",0,"capture_ready")]);Check(calls==beforeHeld+1,"duplicate release cannot enqueue translation twice");
sender.Exchange("session",[],[],refused:[new("held-native","disabled")]);
var beforeSame=calls;var registered=new OutgoingInput("same-packet","registered","all",HoldForInput:true);
sender.Exchange("session",[registered],[],statuses:[Status(3,"same-packet",0,"capture_ready",channel:"all")]);await Ready("same-packet");
Check(calls==beforeSame+1,"registration and stable-close release in one packet start one translation");
sender.Exchange("session",[registered],[],statuses:[Status(3,"same-packet",0,"capture_ready",channel:"all")]);await Task.Delay(25);
Check(calls==beforeSame+1&&View("same-packet").State=="ready","registration plus release retransmission is idempotent after a lost reply");
sender.Exchange("session",[],["same-packet"]);

sender.Exchange("session",[new("first","PRIVATE_SOURCE","team","bilingual")],[]);await Ready("first");
Check(View("first").PartCount==2&&View("first").SendText=="PRIVATE_SOURCE","bilingual separate snapshots keep intentional original-first segment");
Exchange([Status(1,"first",0,"waiting_input","input")]);Check(View("first").State=="waiting_input"&&View("first").WaitReason=="input"&&!View("first").Error!.Contains("确认发送频道"),"native editor wait has its own state and reason");
Exchange([Status(2,"first",0,"waiting_input","settings")]);Check(View("first").Error!.Contains("设置窗口"),"settings ownership has a separate wait reason");
Exchange([Status(3,"first",0,"waiting_channel","channel")]);Check(View("first").State=="waiting_channel","closed input with unknown native target waits for channel");
Exchange([Status(4,"first",0,"ready"),Status(5,"first",0,"dispatched")],[new("first",0),new("first",0)]);
Check(View("first").State=="ready"&&View("first").PartIndex==1&&View("first").SendText=="translated","duplicate first-part ACK advances exactly once");
Check(View("first").PartStatuses![0].State=="dispatched"&&View("first").PartStatuses![1].State=="pending","dispatch observation is per part");
Exchange([Status(5,"first",0,"dispatched")],[new("first",0)]);Check(View("first").PartIndex==1,"repeated ACK and progress cannot advance another part");
Exchange([Status(6,"first",0,"echo_unobserved","timeout")]);Check(View("first").State=="ready"&&View("first").PartIndex==1&&View("first").PartStatuses![0].State=="echo_unobserved","missing echo never cancels the bilingual remainder");
Exchange([Status(7,"first",1,"dispatched")],[new("first",1)]);Check(View("first").State=="submitted"&&View("first").Error==null,"second part completes without any echo confirmation");
Exchange([Status(8,"first",1,"echo_observed")]);Check(View("first").PartStatuses![1].State=="echo_observed","post-dispatch local self row updates only its segment");visibility.Add(View("first"));
Exchange([Status(9,"first",1,"echo_unobserved","timeout")],[new("first",1)]);Check(View("first").PartStatuses![1].State=="echo_observed"&&View("first").State=="submitted","late duplicate progress cannot erase an observation or replay");
Check(calls==beforeHeld+3,"status and duplicate ACK never call translation again");

sender.Exchange("session",[new("old","legacy","all")],[]);await Ready("old");sender.Exchange("session",[],["old"]);
Check(View("old").State=="submitted"&&View("old").PartStatuses==null,"old client ACK remains dispatch only and fabricates no echo");
sender.Exchange("session",[new("long","LONG","party")],[]);await Ready("long");Check(View("long").PartCount==3,"long translated body has three bounded segments");
for(var part=0;part<3;part++){Check(View("long").SendCharacters<=80,"long segment "+part+" stays within native scalar cap");sender.Exchange("session",[],[],partAcks:[new("long",part),new("long",part)]);}
Check(View("long").State=="submitted","long task completes in segment order");

sender.Exchange("session",[new("expiry","retained","team")],[]);await Ready("expiry");Exchange([Status(10,"expiry",0,"waiting_input","input")]);now+=36000;
Check(View("expiry").State=="failed"&&View("expiry").Original=="retained"&&View("expiry").English=="translated","input wait expires retaining full source and translation");
Exchange([Status(11,"expiry",0,"ready")],sent:["expiry"]);Check(View("expiry").State=="failed","closing input after deadline cannot revive or acknowledge expired work");
var beforeRefused=calls;sender.Exchange("session",[new("close","retained-close","team")],[],refused:[new("close","native-close")]);
Check(View("close").State=="failed"&&View("close").Original=="retained-close"&&View("close").Error!.Contains("未能收起"),"close failure may retain a first snapshot and refusal in the same exchange");
await Task.Delay(60);Check(calls==beforeRefused&&View("close").State=="failed"&&View("close").SendText=="","same-exchange close refusal causes zero translation calls and cannot revive");
sender.Exchange("session",[new("fresh","fresh","team")],[]);await Ready("fresh");
Exchange([Status(12,"fresh",0,"waiting_input","input",channel:"all")]);Check(View("fresh").State=="ready","wrong-channel status cannot mutate task state");
Exchange([Status(13,"fresh",0,"echo_observed")]);Check(View("fresh").PartStatuses==null,"echo without dispatched segment is ignored");
Exchange([Status(12,"fresh",0,"dispatched")],sent:["fresh"]);Check(View("fresh").PartStatuses![0].State=="dispatched","invalid higher-sequence echo cannot swallow a lower valid dispatch");
Exchange([Status(13,"fresh",0,"echo_observed")]);Check(View("fresh").PartStatuses![0].State=="echo_observed","retransmitted observation succeeds after its dispatch metadata arrives");
sender.Exchange("session",[new("reorder","source-reorder","team","bilingual")],[]);await Ready("reorder");
Exchange([Status(20,"reorder",0,"waiting_input","input"),Status(21,"reorder",0,"dispatched")],[new("reorder",0)]);
Check(View("reorder").PartIndex==1&&View("reorder").State=="ready","same-packet old wait cannot overwrite the newly advanced part");
Exchange([Status(25,"reorder",1,"dispatched")],[new("reorder",1)]);
Exchange([Status(22,"reorder",0,"echo_observed"),Status(26,"reorder",1,"echo_unobserved","timeout")]);
Check(View("reorder").State=="submitted"&&View("reorder").PartStatuses![0].State=="echo_observed"&&View("reorder").PartStatuses![1].State=="echo_unobserved","late first-part echo survives a newer second-part status without changing terminal task state");
Exchange([Status(27,"reorder",1,"waiting_input","input")],[new("reorder",0),new("reorder",1)]);
Check(View("reorder").State=="submitted","late waits and repeated ACKs cannot reopen a completed task");
var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var finish=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
await using(var slow=new OutgoingService(config,async(text,c,fast,ct)=>{started.TrySetResult();return await finish.Task;})) {
 slow.Exchange("slow",[new("pending","pending-body","team")],[]);await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
 slow.Exchange("slow",[],[],refused:[new("pending","native-close")]);finish.TrySetResult("late translation");await Task.Delay(80);
 Check(slow.Recent.Single().State=="failed"&&slow.Recent.Single().SendText=="","already-running translation finishing after refusal cannot revive failed work");
}
sender.Exchange("new-session",[],[],statuses:[Status(14,"fresh",0,"dispatched")]);Check(sender.Recent.Length==0,"cross-match session clears pending work and ignores stale progress");

var json=JsonSerializer.Serialize(new OutgoingStatus(1,"id",0,"dispatched","team",1),ConfigStore.Json);Check(!json.Contains("text",StringComparison.OrdinalIgnoreCase),"progress protocol carries metadata only");
await log.FlushAsync();var logText=string.Concat((await log.SnapshotAsync()).Select(Encoding.UTF8.GetString));Check(!logText.Contains("PRIVATE_SOURCE")&&!logText.Contains("retained-close"),"dispatch and echo diagnostics never store message bodies");
File.WriteAllText(Path.Combine(root,"artifacts/send-native-20261027/visibility-snapshots.json"),JsonSerializer.Serialize(visibility,ConfigStore.Json));
var output=new{passed=results.Count,total=results.Count,realGame=false,cloudCalls=0,checks=results};File.WriteAllText(Path.Combine(root,"artifacts/send-native-20261027/service-tests.json"),JsonSerializer.Serialize(output,ConfigStore.Json));Console.WriteLine("TOTAL "+results.Count);
