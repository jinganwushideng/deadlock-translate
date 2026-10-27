using DeadlockTranslate;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var folder=Path.Combine(root,"artifacts/optimization-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
int passed=0;
void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);passed++;}
async Task Cancelled(Task task){try{await task;throw new Exception("expected cancellation");}catch(OperationCanceledException){}}
var gate=new PriorityConcurrency(2,true);using(var receive=await gate.Enter(CancellationToken.None)){
 var waiting=gate.Enter(CancellationToken.None);Check(!waiting.IsCompleted,"receive backlog leaves a send slot free");
 using(var send=await gate.Enter(CancellationToken.None,true))Check(!waiting.IsCompleted,"send starts without waiting for a slow receive");
 gate.Configure(3,true);using var more=await waiting.WaitAsync(TimeSpan.FromSeconds(2));Check(true,"limit increase wakes eligible receives");
}
gate.Configure(2,false);using(var a=await gate.Enter(CancellationToken.None))using(var b=await gate.Enter(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)))Check(true,"disabling reservation restores all receive capacity");
var fair=new PriorityConcurrency(1,true);var held=await fair.Enter(CancellationToken.None);
var normal=fair.Enter(CancellationToken.None);var sends=Enumerable.Range(0,4).Select(_=>fair.Enter(CancellationToken.None,true)).ToArray();held.Dispose();
for(int i=0;i<3;i++){(await sends[i]).Dispose();}
using(var turn=await normal.WaitAsync(TimeSpan.FromSeconds(2))){Check(!sends[3].IsCompleted,"three priority sends are followed by a waiting receive");}
(await sends[3]).Dispose();
var cancelGate=new PriorityConcurrency(1,false);var holder=await cancelGate.Enter(CancellationToken.None);using var stopped=new CancellationTokenSource();var abandoned=cancelGate.Enter(stopped.Token,true);stopped.Cancel();await Cancelled(abandoned);holder.Dispose();using(await cancelGate.Enter(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)))Check(true,"cancelled priority waiter does not leak capacity");

using(var memo=new TranslationMemo()){
 var finish=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);int calls=0;
 Task<string> Factory(CancellationToken ct){Interlocked.Increment(ref calls);return finish.Task.WaitAsync(ct);}
 using var firstStop=new CancellationTokenSource();var first=memo.Get("same",TimeSpan.FromSeconds(3),Factory,firstStop.Token);
 var others=Enumerable.Range(0,10).Select(_=>memo.Get("same",TimeSpan.FromSeconds(3),Factory,CancellationToken.None)).ToArray();
 firstStop.Cancel();await Cancelled(first);Check(calls==1&&!others[0].IsCompleted,"one cancelled subscriber does not cancel the shared translation");
 finish.SetResult("译文");await Task.WhenAll(others);Check(others.All(t=>t.Result=="译文")&&calls==1,"identical concurrent requests share one result");
 Check(await memo.Get("same",TimeSpan.FromSeconds(3),_=>throw new Exception("cache miss"),CancellationToken.None)=="译文","successful result is cached");
 int failures=0;for(int i=0;i<2;i++)try{await memo.Get("bad",TimeSpan.FromSeconds(3),_=>{failures++;throw new InvalidDataException();},CancellationToken.None);}catch(InvalidDataException){}
 Check(failures==2,"failed translations are not cached");
 using var allStop=new CancellationTokenSource();var cancelled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
 var discarded=memo.Get("cancel",TimeSpan.FromSeconds(3),async ct=>{try{await Task.Delay(Timeout.Infinite,ct);return "unreachable";}finally{cancelled.TrySetResult();}},allStop.Token);
 allStop.Cancel();await Cancelled(discarded);await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));Check(true,"last subscriber cancellation cancels network work");
 for(int i=0;i<300;i++)await memo.Get("bounded-"+i,TimeSpan.FromSeconds(1),_=>Task.FromResult("ok"),CancellationToken.None);
 Check(JsonSerializer.SerializeToElement(memo.Stats).GetProperty("cached").GetInt32()<=256,"result cache has a fixed capacity");
}
var config=new ConfigStore(Path.Combine(folder,"provider"));config.Save(config.Current with{LlmBaseUrl="https://fixture.invalid/v1",LlmKey="fake-key",Model="fixture",OutgoingMode="quality"});
var terms=new Terms(folder,Path.Combine(folder,"no-default-terms.json"));using var network=new FixtureHttp();using var http=new HttpClient(network);using var provider=new Translators(config,terms,http);
var snapshots=Enumerable.Range(0,10).Select(_=>provider.Quality("same phrase",["context a"],config.Current,CancellationToken.None)).ToArray();await Task.WhenAll(snapshots);Check(network.Calls==1,"provider coalesces equal text and context");
await provider.Quality("same phrase",["context a"],config.Current,CancellationToken.None);Check(network.Calls==1,"provider result cache avoids another HTTP request");
await provider.Quality("same phrase",["context b"],config.Current,CancellationToken.None);Check(network.Calls==2,"different context cannot reuse a translation");
terms.Save([new Term("unused","词条","Unused")]);await provider.Quality("same phrase",["context a"],config.Current,CancellationToken.None);Check(network.Calls==3,"glossary revision invalidates result identity");
config.Save(config.Current with{Model="fixture-2"});await provider.Quality("same phrase",["context a"],config.Current,CancellationToken.None);Check(network.Calls==4,"model/config change cannot reuse old result");
var old=config.Current;config.Save(config.Current with{LlmBaseUrl="https://different.invalid/v1",LlmKey="new-fake-key"});
try{await provider.Quality("same phrase",["context a"],old,CancellationToken.None);throw new Exception("old endpoint allowed");}catch(InvalidOperationException){}
Check(network.Calls==4,"cached results do not bypass stale-endpoint credential guard");
await provider.TranslateOutgoing("发送测试",config.Current,false,CancellationToken.None);await provider.TranslateOutgoing("发送测试",config.Current,false,CancellationToken.None);Check(network.Calls==5,"outgoing translations also reuse identical results");

using var rsa=RSA.Create(2048);var request=new CertificateRequest("CN=DeadlockTranslate localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
using var expired=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3),DateTimeOffset.UtcNow.AddDays(-1));
var expiredState=Path.Combine(folder,"expired");Directory.CreateDirectory(expiredState);ConfigStore.WriteAtomic(Path.Combine(expiredState,"localhost-certificate.dpapi"),ProtectedData.Protect(expired.Export(X509ContentType.Pfx),null,DataProtectionScope.CurrentUser));
var diagnostics=new CertificateDiagnostics(expired,null);Check(!diagnostics.CanServe&&diagnostics.Inspect().State=="expired","expired certificate is diagnosed instead of treated as ready");
Check(new CertificateDiagnostics(null,"fixture").Inspect().State=="unavailable","missing certificate has an actionable diagnostic state");
var certFolder=Path.Combine(folder,"cert");using var cert=LocalHttps.Load(certFolder);var oldProtected=File.ReadAllBytes(Path.Combine(certFolder,"localhost-certificate.dpapi"));
using var renewed=LocalHttps.Load(certFolder,true);Check(cert.Thumbprint!=renewed.Thumbprint,"explicit certificate renewal creates a new identity");
var backup=Directory.GetFiles(Path.Combine(certFolder,"certificate-backups"),"*.dpapi",SearchOption.AllDirectories).Single();Check(oldProtected.SequenceEqual(File.ReadAllBytes(backup)),"renewal preserves original encrypted key backup");
File.WriteAllText(Path.Combine(root,"artifacts/optimization-tests.json"),JsonSerializer.Serialize(new{passed,expiredState,scope="mock provider requests and isolated per-user encrypted certificates; no game input, real API calls or trust-store changes"},ConfigStore.Json));
Console.WriteLine($"TOTAL {passed} passed");

sealed class FixtureHttp:HttpMessageHandler {
 public int Calls;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Interlocked.Increment(ref Calls);await Task.Delay(30,ct);return new(HttpStatusCode.OK){Content=JsonContent.Create(new{choices=new[]{new{message=new{content="模拟译文"},finish_reason="stop"}}})};}
}
