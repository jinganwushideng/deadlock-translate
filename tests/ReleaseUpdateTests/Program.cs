using System.Net;
using System.Security.Cryptography;
using System.Text;
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
        Check(fixture.Checks==1 && fixture.MirrorCalls==0,"successful GitHub checks do not contact mirrors");
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
    clock=clock.AddHours(25);fixture.Limited=true;fixture.QuarkFolder="同频，最新版 0.7.10";
    fixture.BaiduHtml=Fixture.BaiduFolder("deadlock翻译，最新版本：0.7.4.2");
    var limited=await next.Check(true,default);
    Check(limited.Version=="0.7.3.1"&&limited.RetryAt>clock,"rate limit preserves cached release and records retry time");
    Check(limited.MirrorAvailable && limited.MirrorVersion=="0.7.10" &&
        limited.Mirrors is { QuarkVersion:"0.7.10", BaiduVersion:"0.7.4.2" }, "fallback reads both mirror folder names and compares versions numerically");
    next.Dismiss();
    Check(!next.Status().MirrorAvailable && !next.Status().Available, "dismissal hides the current reminder without removing sources");
    await next.Check(false,default);
    Check(fixture.Checks==1 && fixture.MirrorCalls==3,"manual retries respect GitHub retry limit for all sources");
    next.SetAutomatic(false);clock=clock.AddDays(2);await next.Check(true,default);
    Check(fixture.Checks==1 && fixture.MirrorCalls==3,"disable preference prevents future automatic requests");
    using(var fallback = new ReleaseUpdates(Path.Combine(root,"mirror-only"),"0.7.10",fixture,()=>clock)){
        fixture.QuarkFolder="同频，最新版 9.9.9-beta.1";
        fixture.BaiduHtml=Fixture.BaiduFolder("同频，最新版 0.7.11");
        var baidu=await fallback.Check(false,default);
        Check(baidu.MirrorAvailable && baidu.MirrorVersion=="0.7.11" && baidu.Mirrors?.QuarkVersion==null && baidu.Version==null,
            "Baidu can remind on its own and prerelease folders are ignored");
        try { fallback.StartDownload();throw new InvalidOperationException("mirror incorrectly authorized automatic download"); }
        catch(IOException) { Console.WriteLine("PASS mirror folder metadata cannot authorize executable download"); }
        clock=clock.AddHours(1);fixture.QuarkFolder="同频，最新版 0.7.10.0";
        fixture.BaiduHtml="<html>locals.mset(invalid)";
        var stale=await fallback.Check(false,default);
        Check(!stale.MirrorAvailable && stale.MirrorVersion==null && stale.Mirrors is {QuarkVersion:"0.7.10.0",BaiduVersion:null},
            "a malformed mirror leaves the other readable and equivalent versions do not notify");
        clock=clock.AddHours(1);fixture.Limited=false;
        var recovered=await fallback.Check(false,default);
        Check(recovered.CheckError==null && recovered.Mirrors==null, "a successful GitHub check clears fallback results");
    }
    var routing=new RoutingFixture();
    using(var channels=new ReleaseUpdates(Path.Combine(root,"route-fast"),"0.7.5",routing)){
        var fast=await channels.SelectDownloadChannel(default);
        Check(fast.Source=="github"&&fast.Url==ReleaseUpdates.GitHubReleases+"/latest"&&
          fast.SampleBytes==ReleaseUpdates.SpeedSampleBytes&&routing.ReadBytes==ReleaseUpdates.SpeedSampleBytes&&routing.Ranges.All(x=>x=="bytes=0-1048575"),
          "fast real-asset sampling chooses the latest GitHub page and caps reads at 1 MiB");
    }
    routing=new RoutingFixture{Slow=true};
    using(var channels=new ReleaseUpdates(Path.Combine(root,"route-slow"),"0.7.5",routing)){
        var slow=await channels.SelectDownloadChannel(default);
        Check(slow.Source=="quark"&&slow.Reason=="slow"&&slow.Url==ReleaseUpdates.QuarkShare,
          "reachable but slow downloads choose the Quark mirror");
    }
    routing=new RoutingFixture{Fail=true};
    using(var channels=new ReleaseUpdates(Path.Combine(root,"route-offline"),"0.7.5",routing)){
        var offline=await channels.SelectDownloadChannel(default);
        Check(offline.Source=="quark"&&offline.Reason=="unavailable","unavailable GitHub chooses the mirror");
    }
    routing=new RoutingFixture{Html=true};
    using(var channels=new ReleaseUpdates(Path.Combine(root,"route-html"),"0.7.5",routing)){
        Check((await channels.SelectDownloadChannel(default)).Source=="quark","an HTML interception page cannot pass as a fast installer download");
    }
    routing=new RoutingFixture{Unsafe=true};
    using(var channels=new ReleaseUpdates(Path.Combine(root,"route-redirect"),"0.7.5",routing)){
        Check((await channels.SelectDownloadChannel(default)).Source=="quark"&&routing.UnsafeRequests==0,
          "off-host redirects are rejected without requesting the untrusted host");
    }
    routing=new RoutingFixture{Block=true};
    using(var channels=new ReleaseUpdates(Path.Combine(root,"route-cancel"),"0.7.5",routing)){
        using var cancellation=new CancellationTokenSource(100);
        var pending=channels.SelectDownloadChannel(cancellation.Token);
        await routing.Started.Task;
        try{await channels.SelectDownloadChannel(default);throw new Exception("parallel speed probe accepted");}
        catch(IOException){}
        try{await pending;throw new Exception("cancelled probe chose a page");}
        catch(OperationCanceledException){Console.WriteLine("PASS caller cancellation opens no destination and concurrent probes are rejected");}
        routing.Block=false;
        Check((await channels.SelectDownloadChannel(default)).Source=="github","cancelling a probe releases the gate for a later click");
    }
    routing=new RoutingFixture{Block=true};
    using(var channels=new ReleaseUpdates(Path.Combine(root,"route-timeout"),"0.7.5",routing)){
        var time=System.Diagnostics.Stopwatch.StartNew();
        var timed=await channels.SelectDownloadChannel(default);
        Check(timed.Source=="quark"&&timed.Reason=="timeout"&&time.Elapsed<TimeSpan.FromSeconds(8),
          "a hanging GitHub connection falls back after the bounded six-second probe");
    }
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
    internal int MirrorCalls;
    internal string? QuarkFolder, BaiduHtml;
    internal bool Corrupt,Limited,Block;
    internal static string Release(string url) => JsonSerializer.Serialize(new{tag_name="v0.7.3.1",draft=false,prerelease=false,body="fixture summary",assets=new object[]{
        new{name=Name,browser_download_url=url+Name,size=Bytes.Length,digest="sha256:"+Digest},
        new{name=Name+".sha256",browser_download_url=url+Name+".sha256",size=Digest.Length+2+Name.Length+1}
    }});
    internal static string BaiduFolder(string name) => "<title>最新版 99.0.0</title><script>locals.mset(" +
        JsonSerializer.Serialize(new { file_list = new[] { new { isdir = 0, server_filename = "最新版 99.0.0" }, new { isdir = 1, server_filename = name } } }) +
        ");</script>";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
        if(request.RequestUri!.Host=="api.github.com"){
            Checks++;
            return Limited?new(HttpStatusCode.TooManyRequests):new(HttpStatusCode.OK){Content=new StringContent(Release(Base))};
        }
        if(request.RequestUri.Host=="drive-h.quark.cn"){
            MirrorCalls++;
            var token=request.RequestUri.AbsolutePath.EndsWith("/token");
            return new(HttpStatusCode.OK){Content=new StringContent(token
                ? """{"code":0,"data":{"stoken":"fixture&token"}}"""
                : JsonSerializer.Serialize(new { code = 0, data = new { list = new[] {
                    new { dir = true, file_name = QuarkFolder }, new { dir = false, file_name = (string?)"最新版 99.0.0" }
                } } }))};
        }
        if(request.RequestUri.Host=="pan.baidu.com"){
            MirrorCalls++;
            return new(HttpStatusCode.OK){Content=new StringContent(BaiduHtml??"",Encoding.UTF8)};
        }
        if(Block)await Task.Delay(Timeout.InfiniteTimeSpan,ct);
        return new(HttpStatusCode.OK){Content=request.RequestUri.AbsolutePath.EndsWith(".sha256")
            ?new StringContent(Digest+"  "+Name+"\n")
            :new ByteArrayContent(Corrupt?[12,24,41]:Bytes)};
    }
}


sealed class RoutingFixture:HttpMessageHandler {
    public bool Slow,Fail,Html,Unsafe,Block;
    public int ReadBytes,UnsafeRequests;
    public readonly List<string> Ranges=[];
    public readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
    const string Asset="https://github.com/jinganwushideng/deadlock-translate/releases/download/v0.7.5/DeadlockTranslate-Setup-0.7.5.exe";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
        Started.TrySetResult();
        if(Fail)throw new HttpRequestException("fixture offline");
        if(Block)await Task.Delay(Timeout.InfiniteTimeSpan,ct);
        if(request.RequestUri!.Host=="api.github.com")
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{
                tag_name="v0.7.5",draft=false,prerelease=false,assets=new[]{
                    new{name="DeadlockTranslate-Setup-0.7.5.exe",browser_download_url=Asset,size=8*1024*1024},
                    new{name="DeadlockTranslate-Setup-0.7.5.exe.sha256",browser_download_url=Asset+".sha256",size=128}
                }}))};
        Ranges.Add(request.Headers.Range?.ToString()??"");
        if(request.RequestUri.Host=="github.com")
            return new(HttpStatusCode.Redirect){Headers={Location=new Uri(Unsafe?"https://untrusted.example/installer.exe":"https://release-assets.githubusercontent.com/fixture")}};
        if(request.RequestUri.Host!="release-assets.githubusercontent.com"){UnsafeRequests++;throw new Exception("untrusted host requested");}
        if(Slow)await Task.Delay(2300,ct);
        if(Html)return new(HttpStatusCode.OK){Content=new StringContent("<html>fixture interception</html>")};
        // Intentionally ignore Range and expose a larger stream, as some proxies do.
        return new(HttpStatusCode.OK){Content=new StreamContent(new SampleStream(this))};
    }
    sealed class SampleStream(RoutingFixture owner):Stream {
        long position;
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>8*1024*1024;public override long Position{get=>position;set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count){
            var size=(int)Math.Min(count,Length-position);
            Array.Fill<byte>(buffer,0,offset,size);
            if(position==0&&size>0)buffer[offset]=(byte)'M';
            if(position<=1&&position+size>1)buffer[offset+(int)(1-position)]=(byte)'Z';
            position+=size;owner.ReadBytes+=size;return size;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){
            token.ThrowIfCancellationRequested();
            var data=new byte[buffer.Length];var size=Read(data,0,data.Length);data.AsSpan(0,size).CopyTo(buffer.Span);
            return ValueTask.FromResult(size);
        }
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
