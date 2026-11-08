using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DeadlockTranslate;

var root = Path.Combine(Path.GetTempPath(), "DLT-update-fixture-" + Guid.NewGuid().ToString("N"));
var clock = DateTimeOffset.UtcNow;
var fixture = new Fixture();
void Check(bool condition,string message){if(!condition)throw new Exception(message);Console.WriteLine("PASS "+message);}
async Task WaitDownload(ReleaseUpdates service){
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while(service.Status().Download=="downloading")await Task.Delay(20,timeout.Token);
}
try {
    Check(ReleaseUpdates.ParseVersion("v0.7.3") == ReleaseUpdates.ParseVersion("0.7.3.0"), "normalizes three/four part versions");
    Check(ReleaseUpdates.ParseVersion("v0.7.3-beta.1") == null, "rejects prerelease tags");
    using(var service = new ReleaseUpdates(root, "0.7.2", fixture, ()=>clock)){
        var view = await service.Check(true,default);
        Check(view.Available && view.Version=="0.7.3.1", "new official release is reported");
        await service.Check(true,default);
        Check(fixture.Checks==1,"automatic checks are throttled");
        service.Dismiss();Check(!service.Status().Available,"dismissal hides that version reminder");
        service.StartDownload();await WaitDownload(service);
        Check(service.Status().Download=="ready","matches size, checksum and asset digest");
        fixture.Corrupt=true;
        service.StartDownload();await WaitDownload(service);
        Check(service.Status().Download=="failed","rejects corrupted download");
        fixture.Corrupt=false;fixture.Block=true;
        service.StartDownload();service.CancelDownload();await WaitDownload(service);
        Check(service.Status().Download=="cancelled","cancellation does not leave a downloadable partial file");
    }
    fixture=new Fixture();
    using var next = new ReleaseUpdates(root,"0.7.2",fixture,()=>clock);
    await next.Check(true,default);
    Check(fixture.Checks==0,"daily throttle survives restarting the service");
    clock=clock.AddHours(25);fixture.Limited=true;
    var limited=await next.Check(true,default);
    Check(limited.Version=="0.7.3.1"&&limited.RetryAt>clock,"rate limit preserves cached release and records retry time");
    await next.Check(false,default);
    Check(fixture.Checks==1,"manual retries respect GitHub retry limit");
    next.SetAutomatic(false);clock=clock.AddDays(2);await next.Check(true,default);
    Check(fixture.Checks==1,"disable preference prevents future automatic requests");
    using var unsafeRelease=JsonDocument.Parse(Fixture.Release("https://untrusted.example/"));
    Check(ReleaseUpdates.ReadRelease(unsafeRelease.RootElement)==null,"rejects off-repository asset links");
}finally{if(Directory.Exists(root))Directory.Delete(root,true);}

sealed class Fixture : HttpMessageHandler
{
    private const string Base = "https://github.com/jinganwushideng/deadlock-translate/releases/download/v0.7.3.1/";
    private const string Name = "DeadlockTranslate-Setup-0.7.3.1.exe";
    private static readonly byte[] Bytes = [12, 24, 42];
    private static readonly string Digest=Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
    internal int Checks;
    internal bool Corrupt,Limited,Block;
    internal static string Release(string url) => JsonSerializer.Serialize(new{tag_name="v0.7.3.1",draft=false,prerelease=false,body="fixture summary",assets=new object[]{
        new{name=Name,browser_download_url=url+Name,size=Bytes.Length,digest="sha256:"+Digest},
        new{name=Name+".sha256",browser_download_url=url+Name+".sha256",size=Digest.Length+2+Name.Length+1}
    }});
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
        if(request.RequestUri!.Host=="api.github.com"){
            Checks++;
            return Limited?new(HttpStatusCode.TooManyRequests):new(HttpStatusCode.OK){Content=new StringContent(Release(Base))};
        }
        if(Block)await Task.Delay(Timeout.InfiniteTimeSpan,ct);
        return new(HttpStatusCode.OK){Content=request.RequestUri.AbsolutePath.EndsWith(".sha256")
            ?new StringContent(Digest+"  "+Name+"\n")
            :new ByteArrayContent(Corrupt?[12,24,41]:Bytes)};
    }
}
