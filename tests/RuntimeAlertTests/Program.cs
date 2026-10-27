using DeadlockTranslate;
using System.Net;
using System.Text.Json;

int passed=0;
void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
var calls=0;long now=0;
var state=new GamePrivilegeStatus(true,true,true);
var probe=new GamePrivilegeDiagnostics(()=>{calls++;return state;},()=>now);
Check(probe.Inspect().Elevated==true&&calls==1,"permissions are read without relying on foreground focus");
state=new(false,null,null);now=2999;Check(probe.Inspect().Detected==true&&calls==1,"frequent settings polls reuse cached process results");
now=3000;Check(probe.Inspect().Detected==false&&calls==2,"game exit refreshes old privilege evidence");
state=new(true,null,null);Check(probe.Inspect(true).Elevated==null&&calls==3,"unknown permissions remain unknown on forced check");
var broken=new GamePrivilegeDiagnostics(()=>throw new UnauthorizedAccessException());Check(broken.Inspect().Detected==null,"access failure is not reported as game exit or permission mismatch");
Check(StartupCompatibility.ProcessElevated(Environment.ProcessId)==StartupCompatibility.Elevated(),"limited token query reads this process elevation");
Check(StartupCompatibility.ProcessElevated(int.MaxValue)==null,"unreadable process has no guessed elevation");

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var data=Path.Combine(root,"artifacts","runtime-alert-tests-"+Guid.NewGuid().ToString("N"));
var config=new ConfigStore(data);config.Save(config.Current with{OutgoingEnabled=true,OutgoingMode="fast",OutgoingFallbackFast=false});
await using var service=new OutgoingService(config,(text,_,_,_)=>text=="failure"?Task.FromException<string>(new HttpRequestException("SECRET provider detail",null,HttpStatusCode.Unauthorized)):Task.FromResult("English"));
service.Exchange("test",[new("failed","failure","team")],[]);
for(var i=0;i<100&&service.Diagnostics.State!="failed";i++)await Task.Delay(10);
Check(service.Diagnostics.FailureCode=="Unauthorized","service authentication failure has a structured diagnosis");
var json=JsonSerializer.Serialize(service.Diagnostics);Check(!json.Contains("failure")&&!json.Contains("SECRET")&&!json.Contains("English"),"sending diagnostics contain no chat or provider exception text");
service.Exchange("test",[new("good","safe","team")],[]);
for(var i=0;i<100&&service.Diagnostics.State!="ready";i++)await Task.Delay(10);
service.Exchange("test",[],["good"]);Check(service.Diagnostics.State=="submitted"&&service.Diagnostics.FailureCode==null,"later successful send clears the previous error diagnosis");
service.Exchange("test",[new("refused","safe","team")],[]);
for(var i=0;i<100&&service.Diagnostics.State!="ready";i++)await Task.Delay(10);
service.Exchange("test",[],[],null,[new("refused","channel-fault")]);Check(service.Diagnostics.RefusalCode=="channel-fault","channel refusal remains distinct from provider or privilege failure");
service.Exchange("new-session",[],[]);Check(service.Diagnostics.State=="idle","new session does not retain previous send fault");
File.WriteAllText(Path.Combine(root,"artifacts","runtime-alert-unit-tests.json"),JsonSerializer.Serialize(new{passed,realUac=false,realApi=false,realGameInput=false}));
Console.WriteLine("PASS TOTAL "+passed);
