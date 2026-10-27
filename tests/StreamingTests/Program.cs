using DeadlockTranslate;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));var temp=Path.Combine(root,"artifacts/streaming-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
int passed=0;void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);passed++;Console.WriteLine("PASS: "+name);}
async Task Until(Func<bool> ready){var until=Environment.TickCount64+3000;while(!ready()&&Environment.TickCount64<until)await Task.Delay(5);Check(ready(),"asynchronous phase reached");}
if(args.Contains("--live")){
 var state=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DeadlockTranslate");
 using(var settingsJson=JsonDocument.Parse(File.ReadAllText(Path.Combine(state,"settings.json"))))if(!settingsJson.RootElement.TryGetProperty("incomingScope",out _))throw new InvalidOperationException("Refuse to migrate personal configuration");
 var personal=new ConfigStore(state);var snapshot=personal.Current with{LlmThinkingEnabled=!args.Contains("--off"),LlmThinkingApi="auto",LlmStreaming=true,OutgoingUseContext=true,OutgoingMode="quality"};
 using var handler=new HttpClientHandler{AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.All};using var network=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
 var glossary=new Terms(temp,Path.Combine(root,"src/Bridge/data/terms.json"));using var translator=new Translators(personal,glossary,network);using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(35));
 var message="阿波罗出个跑酷，用来应对减速魔咒";bool success=false;string? failure=null;
 try{var output=await translator.TranslateOutgoing(message,["对面用了减速魔咒","阿波罗要补一件装备"],snapshot,false,stop.Token);success=output.Contains("Apollo")&&output.Contains("Stamina Mastery")&&output.Contains("Slowing Hex");}catch(Exception ex){failure=ex.GetType().Name;}
 var report=new{success,failure,request=translator.Telemetry.Recent.FirstOrDefault(),scope="synthetic sentence and two synthetic context lines only; personal settings unmodified; no raw translation, reasoning, URL or key exported"};
 var file=Path.Combine(root,"artifacts",args.Contains("--off")?"streaming-live-off.json":"streaming-live-on.json");File.WriteAllText(file,JsonSerializer.Serialize(report,ConfigStore.Json));Console.WriteLine(JsonSerializer.Serialize(report,ConfigStore.Json));return;
}
var cfg=new ConfigStore(Path.Combine(temp,"config"));cfg.Save(cfg.Current with{LlmBaseUrl="https://api.deepseek.com/v1",Model="deepseek-flash",LlmKey="fake-local-key",OutgoingMode="quality",OutgoingUseContext=true});
var terms=new Terms(temp,Path.Combine(temp,"none"));await using var log=new DiagnosticLog(Path.Combine(temp,"logging"));
using var source=new Feed();using var fixture=new Handler((_,_)=>Task.FromResult(StreamResponse(source)));using var http=new HttpClient(fixture);using var service=new Translators(cfg,terms,http,log);
var task=service.TranslateOutgoing("synthetic current",["same-channel context"],cfg.Current,false,CancellationToken.None);
await Until(()=>service.Telemetry.Recent.FirstOrDefault()?.Phase=="waiting");
source.Put(Chunk(new{reasoning_content="PRIVATE_REASONING"}));await Until(()=>service.Telemetry.Recent[0].Phase=="thinking");Check(!task.IsCompleted,"thinking chunks do not complete a translation");
source.Put(Chunk(new{content="translated "}));await Until(()=>service.Telemetry.Recent[0].Phase=="generating");Check(!task.IsCompleted,"partial translation cannot be returned or sent");
source.Put(": keepalive\n\ndata: {\"choices\":[{\"index\":0,\n"+"data: \"delta\":{\"content\":\"current\"},\"finish_reason\":null}]}\n\n");
source.Put("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
source.Put("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":30,\"completion_tokens_details\":{\"reasoning_tokens\":20}}}\n\ndata: [DONE]\n\n");source.End();
Check(await task=="translated current","multi-line SSE and usage-only final chunks form one complete answer");
var view=service.Telemetry.Recent.Single();Check(view.Phase=="done"&&view.Transport=="stream"&&view.ContextCount==1,"completed stream records bounded context and transport");
Check(view.InputTokens==100&&view.OutputTokens==30&&view.ReasoningTokens==20&&view.FinishReason=="stop","actual service token usage and finish reason survive parsing");
Check(view.FirstContentMs>=view.FirstResponseMs&&view.FirstResponseMs>=0,"first headers and first translation timing are distinct");
using(var body=JsonDocument.Parse(fixture.LastBody!)){
 Check(body.RootElement.GetProperty("stream").GetBoolean()&&body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean(),"stream request asks for usage statistics");
 Check(body.RootElement.GetProperty("thinking").GetProperty("type").GetString()=="enabled","default explicitly preserves official thinking");
 using var input=JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);Check(input.RootElement.GetProperty("context")[0].GetString()=="same-channel context","outgoing prompt includes only selected context data");
}
await log.FlushAsync();var raw=string.Concat((await log.SnapshotAsync()).Select(b=>Encoding.UTF8.GetString(b)));Check(raw.Contains("LlmUsage")&&raw.Contains("reasoningTokens\":20")&&!raw.Contains("PRIVATE_REASONING")&&!raw.Contains("synthetic current")&&!raw.Contains("same-channel context")&&!raw.Contains("fake-local-key"),"usage log contains numbers and stages without chat/reasoning/key");
var count=fixture.Calls;Check(await service.TranslateOutgoing("synthetic current",["same-channel context"],cfg.Current,false,CancellationToken.None)=="translated current"&&fixture.Calls==count,"streaming retains exact-context request cache");
foreach(var (api,host,model,enabled,expected) in new[]{("auto","https://api.deepseek.com/v1","deepseek-flash",false,"disabled"),("auto","https://api.deepseek.com.evil.invalid/v1","deepseek-flash",false,""),("auto","https://api.deepseek.com:444/v1","deepseek-flash",false,""),("auto","https://api.deepseek.com/v1","unknown",false,""),("deepseek","https://fixture.invalid/v1","fixture",true,"enabled"),("provider","https://api.deepseek.com/v1","deepseek-flash",false,"")}){
 using var content=LlmRequestOptions.Create(model,"system","input",4096,true,enabled,api,host);using var body=JsonDocument.Parse(await content.ReadAsStringAsync());var actual=body.RootElement.TryGetProperty("thinking",out var t)?t.GetProperty("type").GetString():"";Check(actual==expected,"thinking control obeys exact host/model or explicit format: "+api+" / "+host);
}
Check(ApiConfigurationDiagnostics.Inspect(cfg,new("llm",BaseUrl:"https://fixture.invalid/v1",ThinkingEnabled:false)).Checks.Any(c=>c.Field=="thinking"&&c.Level=="warning"),"unsupported auto control has an explicit warning");
Check(!ApiConfigurationDiagnostics.Inspect(cfg,new("llm",ThinkingApi:"bad")).Ready,"invalid thinking format is blocked before cloud request");
Check(ApiConfigurationDiagnostics.HttpFailure("llm",HttpStatusCode.BadRequest,"{\"error\":{\"param\":\"thinking\",\"message\":\"unsupported parameter\"}}").Field=="thinking","unsupported thinking errors point at the correct setting");
Check(ApiConfigurationDiagnostics.HttpFailure("llm",HttpStatusCode.BadRequest,"{\"error\":{\"param\":\"stream_options\",\"message\":\"unsupported parameter\"}}").Field=="streaming","unsupported streaming errors point at the correct setting");
foreach(var (text,reason) in new[]{(Chunk(new{reasoning_content="PRIVATE_REASONING"},"length")+"data: [DONE]\n\n",LogReason.ReasoningBudget),(Chunk(new{content="partial"},"length")+"data: [DONE]\n\n",LogReason.OutputTruncated)}){
 using var badHttp=new HttpClient(new Handler((_,_)=>Task.FromResult(StreamResponse(new MemoryStream(Encoding.UTF8.GetBytes(text))))));using var bad=new Translators(cfg,terms,badHttp);
 try{await bad.TranslateOutgoing("bad output",cfg.Current,false,CancellationToken.None);throw new Exception("expected output failure");}catch(TranslationOutputException ex){Check(ex.Reason==reason&&bad.Telemetry.Recent[0].Phase=="failed","reasoning exhaustion/truncation rejects partial output with metadata");}
}
foreach(var broken in new[]{Chunk(new{content="partial"}),Chunk(new{content="partial"},"stop"),Chunk(new{content="partial"})+"data: [DONE]\n\n"}){
 using var badHttp=new HttpClient(new Handler((_,_)=>Task.FromResult(StreamResponse(new MemoryStream(Encoding.UTF8.GetBytes(broken))))));using var bad=new Translators(cfg,terms,badHttp);
 try{await bad.TranslateOutgoing("broken stream",cfg.Current,false,CancellationToken.None);throw new Exception("expected incomplete stream");}catch(ProviderNetworkException ex){Check(ex.HttpRequestError==HttpRequestError.ResponseEnded&&bad.Telemetry.Recent[0].Phase=="failed","missing finish/DONE/connection closure cannot yield a sendable translation and has a network diagnosis");}
}
using(var waiting=new Feed())using(var cancelHttp=new HttpClient(new Handler((_,_)=>Task.FromResult(StreamResponse(waiting)))))using(var cancelled=new Translators(cfg,terms,cancelHttp)){
 using var stop=new CancellationTokenSource();var cancelledTask=cancelled.TranslateOutgoing("cancel stream",cfg.Current,false,stop.Token);await Until(()=>cancelled.Telemetry.Recent.Length>0);stop.Cancel();try{await cancelledTask;throw new Exception("expected cancellation");}catch(OperationCanceledException){await Until(()=>cancelled.Telemetry.Recent[0].Phase=="cancelled");Check(true,"cancellation aborts streaming without completing or caching partial output");}
}
using(var jsonHttp=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new{choices=new[]{new{message=new{content="json answer"},finish_reason="stop"}}})}))))using(var json=new Translators(cfg,terms,jsonHttp)){
 Check(await json.TranslateOutgoing("json fallback",cfg.Current,false,CancellationToken.None)=="json answer","service ignoring stream returns JSON in the same request");var v=json.Telemetry.Recent[0];Check(v.Transport=="json"&&v.InputTokens==null&&v.ReasoningTokens==null,"omitted usage stays unknown, not zero");
}
long time=0;var history=new ChannelHistory(()=>time);history.Reset("a");history.Add("a","team","team one");history.Add("a","all","enemy all");history.Add("a","party","party one");Check(history.Recent("a","team",3,30).SequenceEqual(new[]{"team one"}),"team/all/party histories cannot mix");
time=31000;Check(history.Recent("a","team",3,30).Length==0,"outgoing context expires within selected time range");history.Reset("b");Check(history.Recent("a","party",3,30).Length==0&&history.Recent("b","party",3,30).Length==0,"session reset prevents cross-match context");
using var noHttp=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new{choices=new[]{new{message=new{content="OK"},finish_reason="stop"}}})})));using var real=new Translators(cfg,terms,noHttp);await using var engine=new JobEngine(cfg,real);
var skipped=engine.Exchange("session",[new("1","中文出装讨论","","team",true),new("2","all line","","all",true)],[]);Check(skipped.All(v=>v.Done&&v.Stage=="skipped"),"context-only Chinese messages do not invoke translations");Check(engine.OutgoingContext("session","team",cfg.Current).SequenceEqual(new[]{"中文出装讨论"}),"context-only Chinese reaches outgoing channel history");Check(engine.OutgoingContext("session","team",cfg.Current with{OutgoingUseContext=false}).Length==0,"disabled outgoing context returns no chat");
await using(var outgoing=new OutgoingService(cfg,(t,c,f,ct)=>Task.FromResult("base"),context:engine.OutgoingContext,translateContext:(t,context,c,f,ct)=>Task.FromResult(context.Single()))){outgoing.Exchange("session",[new("draft","draft","team")],[]);await Until(()=>outgoing.Recent.Single().State=="ready");Check(outgoing.Recent.Single().Translation=="中文出装讨论","outgoing service captures context at submission and passes it to translation");}
engine.Exchange("new",[],[]);Check(engine.OutgoingContext("session","team",cfg.Current).Length==0,"backend match switch discards previous context");
var small=new LlmTelemetry();for(var i=0;i<100;i++){var t=small.Begin(cfg.Current,false,0);t.Usage(null,null,null,"PRIVATE_FINISH");t.Finish("done");}Check(small.Recent.Length==12&&small.Recent.All(v=>v.FinishReason=="other"),"recent diagnostics are bounded and finish reason cannot leak arbitrary response text");
var export=await DiagnosticExport.Create(temp,log,new{requests=service.Telemetry.Recent},cfg.Current);using(var z=System.IO.Compression.ZipFile.OpenRead(export)){using var reader=new StreamReader(z.GetEntry("settings-summary.json")!.Open());var summary=await reader.ReadToEndAsync();Check(summary.Contains("llmMaxTokens")&&summary.Contains("llmThinkingEnabled")&&summary.Contains("outgoingTimeoutSeconds")&&summary.Contains("outgoingContextSeconds"),"diagnostic export includes output budget, thinking, context and send deadline settings");}
File.WriteAllText(Path.Combine(root,"artifacts/streaming-tests.json"),JsonSerializer.Serialize(new{passed,realNetwork=false,at=DateTime.UtcNow},ConfigStore.Json));Console.WriteLine("TOTAL "+passed+" passed");

static string Chunk(object delta,string? finish=null)=>"data: "+JsonSerializer.Serialize(new{choices=new[]{new{index=0,delta,finish_reason=finish}}})+"\n\n";
static HttpResponseMessage StreamResponse(Stream stream){var content=new StreamContent(stream);content.Headers.ContentType=new MediaTypeHeaderValue("text/event-stream");return new(HttpStatusCode.OK){Content=content};}
sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> action):HttpMessageHandler{
 public string? LastBody;public int Calls;protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Interlocked.Increment(ref Calls);LastBody=await request.Content!.ReadAsStringAsync(ct);return await action(request,ct);}
}
sealed class Feed:Stream{
 private readonly Channel<byte[]> parts=Channel.CreateUnbounded<byte[]>();private byte[]? current;private int offset;
 public void Put(string text)=>parts.Writer.TryWrite(Encoding.UTF8.GetBytes(text));public void End()=>parts.Writer.TryComplete();
 public override async ValueTask<int> ReadAsync(Memory<byte> target,CancellationToken ct=default){if(current==null||offset==current.Length){if(!await parts.Reader.WaitToReadAsync(ct))return 0;current=await parts.Reader.ReadAsync(ct);offset=0;}var n=Math.Min(target.Length,current.Length-offset);current.AsMemory(offset,n).CopyTo(target);offset+=n;return n;}
 public override Task<int> ReadAsync(byte[] b,int o,int n,CancellationToken ct)=>ReadAsync(b.AsMemory(o,n),ct).AsTask();public override int Read(byte[] b,int o,int n)=>throw new NotSupportedException();public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>0;set=>throw new NotSupportedException();}public override void Flush(){}public override long Seek(long o,SeekOrigin so)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();
}
