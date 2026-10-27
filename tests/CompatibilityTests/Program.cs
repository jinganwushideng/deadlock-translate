using DeadlockTranslate;
using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

int passed=0;void Check(bool condition,string name){if(!condition)throw new Exception("FAIL: "+name);passed++;Console.WriteLine("PASS: "+name);}
async Task Fault(Func<Task> action,string expected,string name){try{await action();}catch(StartupFault fault){Check(fault.Code==expected,name);return;}throw new Exception("Expected fault: "+name);}
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var owner=StartupCompatibility.Owner();
Check(!StartupCompatibility.RequiresElevation(false,false)&&!StartupCompatibility.RequiresElevation(false,true),"normal entry never requests elevation");
Check(StartupCompatibility.RequiresElevation(true,false)&&!StartupCompatibility.RequiresElevation(true,true),"only opt-in low privilege requests UAC once");
StartupCompatibility.ValidateOwner(owner,owner);StartupCompatibility.ValidateOwner(null,owner);
Check(owner.Length==64&&owner.All(Uri.IsHexDigit),"scope stores a hash rather than raw Windows SID");
Check(StartupCompatibility.SameUser(Environment.ProcessId)==true&&StartupCompatibility.SameUser(int.MaxValue)==null,"token-owner diagnostic reads minimal rights and treats denied lookup as unknown");
Check(StartupCompatibility.Scope(Path.Combine(root,"artifacts"))==StartupCompatibility.Scope(Path.Combine(root,"ARTIFACTS")+"\\"),"same Windows directory has stable scope");
await Fault(()=>{StartupCompatibility.ValidateOwner(new string('0',64),owner);return Task.CompletedTask;},"different-user","another administrator account is rejected before accessing keys");
var launchArgs=new[]{"--admin-compatible","--port","17868","--steam-game",Environment.ProcessPath!,"--admin-compatible","--data-dir","game argument","尾部\\","quote\"literal","& literal"};
var info=StartupCompatibility.ElevationInfo(Environment.ProcessPath!,launchArgs,Path.Combine(root,"artifacts","带空格"),owner);
var forwarded=info.ArgumentList.ToArray();var split=SteamGameLaunch.Split(forwarded);
Check(info.UseShellExecute&&info.Verb=="runas","opt-in delegates permission consent to Windows");
Check(split.Game!.Arguments.SequenceEqual(launchArgs[5..]),"game arguments survive elevation verbatim and are never shell code");
Check(split.Backend.Count(x=>x=="--admin-compatible")==1&&split.Backend.Contains("--elevation-owner"),"backend owner guard is added before Steam command");
Check(split.Backend[Array.IndexOf(split.Backend,"--data-dir")+1]==Path.Combine(root,"artifacts","带空格"),"user data directory pinned despite changed elevation environment");
await Fault(()=>{StartupCompatibility.ElevationInfo(Environment.ProcessPath!,["--admin-compatible","--elevation-owner",owner],root,owner);return Task.CompletedTask;},"elevation-loop","failed elevation cannot repeatedly display UAC");
Check(StartupCompatibility.Reason(new Win32Exception(740))==LogReason.ElevationRequired&&StartupCompatibility.Message(new Win32Exception(740)).Contains("--admin-compatible"),"Windows error 740 has actionable startup guidance");
Check(StartupCompatibility.Reason(new Win32Exception(1223))==LogReason.UacCancelled,"cancelled UAC is distinct from network/translation errors");
Check(!StartupCompatibility.Message(new Exception("SECRET_PROVIDER_KEY")).Contains("SECRET_PROVIDER_KEY"),"startup dialogs never include arbitrary exception or credential text");
Check(GameProcess.ImagePath(Environment.ProcessId)==Environment.ProcessPath&&GameProcess.ImagePath(int.MaxValue)==null,"minimal-rights image lookup succeeds and access failures remain unknown");
var scope=new InstanceScope(owner,StartupCompatibility.Scope(root),StartupCompatibility.Scope(root));
Check(InstanceCoordinator.Matches(scope,scope)&&!InstanceCoordinator.Matches(scope with{Owner=new string('0',64)},scope)&&!InstanceCoordinator.Matches(scope with{Data="other"},scope)&&!InstanceCoordinator.Matches(scope with{Runtime="other"},scope),"owner, data and runtime scopes must all match");
const string origin="http://localhost:17868",key="TEST_PAIRING";
using(var http=new HttpClient(new FakeHandler(_=>throw new Exception("must not probe an empty port")))){
 Check(await InstanceCoordinator.Prepare(http,origin,key,scope,false,false,_=>false)==null,"empty listener table skips TCP connection delay and leaves certificates untouched");
}
using(var http=new HttpClient(new FakeHandler(_=>throw new HttpRequestException(HttpRequestError.ConnectionError,"offline")))){
 Check(await InstanceCoordinator.Prepare(http,origin,key,scope,false,false,_=>true)==null,"absent listener permits a fresh startup");
}
using(var http=new HttpClient(new FakeHandler(_=>new(HttpStatusCode.OK){Content=JsonContent.Create(new{app="OtherProduct"})}))){await Fault(async()=>{await InstanceCoordinator.Prepare(http,origin,key,scope,false,false,_=>true);},"port-busy","unrelated listener is never adopted or stopped");}
using(var http=new HttpClient(new FakeHandler(req=>req.RequestUri!.AbsolutePath=="/health"?Health():new(HttpStatusCode.NotFound)))){await Fault(async()=>{await InstanceCoordinator.Prepare(http,origin,key,scope,true,true,_=>true);},"instance-mismatch","old backend or wrong pairing yields a clear failure");}
using(var http=new HttpClient(new FakeHandler(req=>req.RequestUri!.AbsolutePath=="/health"?Health():new(HttpStatusCode.Conflict)))){await Fault(async()=>{await InstanceCoordinator.Prepare(http,origin,key,scope,true,true,_=>true);},"scope-mismatch","cross-directory/user handoff is rejected");}
foreach(var admin in new[]{false,true}){
 var calls=0;using var http=new HttpClient(new FakeHandler(req=>{calls++;return req.RequestUri!.AbsolutePath=="/health"?Health():Info(admin);}));
 Check(await InstanceCoordinator.Prepare(http,origin,key,scope,admin,admin,_=>true)!=null&&calls==2,"matching existing instance is reused without stopping it "+admin);
}
using(var http=new HttpClient(new FakeHandler(req=>req.RequestUri!.AbsolutePath=="/health"?Health():Info(false)))){await Fault(async()=>{await InstanceCoordinator.Prepare(http,origin,key,scope,true,false,_=>true);},"admin-unavailable","normal process cannot request takeover merely by changing flag");}
var stopped=false;
using(var http=new HttpClient(new FakeHandler(req=>{
 if(req.RequestUri!.AbsolutePath=="/health"){if(stopped)throw new HttpRequestException(HttpRequestError.ConnectionError,"gone");return Health();}
 if(req.RequestUri.AbsolutePath.EndsWith("/admin-handoff")){stopped=true;return new(HttpStatusCode.OK);}
 return Info(false);
}))){Check(await InstanceCoordinator.Prepare(http,origin,key,scope,true,true,_=>true)==null&&stopped,"explicit same-scope elevated handoff waits for old listener to close");}
File.WriteAllText(Path.Combine(root,"artifacts","compatibility-tests.json"),JsonSerializer.Serialize(new{passed,realUacUsed=false,realGameInput=false}));Console.WriteLine("PASS TOTAL "+passed);
static HttpResponseMessage Health()=>new(HttpStatusCode.OK){Content=JsonContent.Create(new{app="DeadlockTranslate"})};
static HttpResponseMessage Info(bool elevated)=>new(HttpStatusCode.OK){Content=JsonContent.Create(new InstanceInfo(typeof(InstanceCoordinator).Assembly.GetName().Version!.ToString(3),elevated,!elevated))};
sealed class FakeHandler(Func<HttpRequestMessage,HttpResponseMessage> response) : HttpMessageHandler {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(response(request));}
