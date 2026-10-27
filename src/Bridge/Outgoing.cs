using System.Threading.Channels;
using System.Text.RegularExpressions;

namespace DeadlockTranslate;
public record OutgoingInput(string Id,string Text,string Channel,string DisplayMode="translation",string? TargetLanguage=null,string? CustomLanguage=null,string? CustomLanguageCode=null,bool? SeparateBilingual=null,bool? SplitLong=null,bool HoldForInput=false);
public record OutgoingPartAck(string Id,int Part);
// No chat bodies: optional client observation protocol, independent of dispatch ACKs.
public record OutgoingStatus(long Seq,string Id,int Part,string State,string Channel,long At,string? Reason=null);
public record OutgoingPartStatus(int Part,string State="pending",long? SubmittedAt=null,long? ObservedAt=null,string? Reason=null);
public record OutgoingRefusal(string Id,string Reason);
public sealed record OutgoingDiagnostics(string State,string? FailureCode,string? RefusalCode,string? NetworkError=null,int? SocketError=null);
public record OutgoingView(string Id,string Original,string English,string Channel,string State,string? Error,string DisplayMode="translation",string SendText="",string TargetLanguage="en",string TargetLanguageName="英语",int PartIndex=0,int PartCount=1,bool IsPreset=false) {
    // English remains on the wire for older clients; new settings render the language-neutral field.
    public string Translation=>English;
    public int SendCharacters=>OutgoingParts.CharacterCount(SendText);
    public int SendCharacterLimit=>OutgoingParts.Limit;
    public int OmittedCharacters {get;init;}
    public bool Truncated=>OmittedCharacters>0;
    public string? WaitReason {get;init;}
    public OutgoingPartStatus[]? PartStatuses {get;init;}
}
public sealed class OutgoingJob(OutgoingInput input,CancellationToken token,Settings config) {
    public OutgoingInput Input=input;public CancellationToken Token=token;public long Created=Environment.TickCount64;
    public Settings Config=config;
    public string[] Context=[];
    public string[] Parts=[];
    public bool SupportsMultipart=true;
    public long Sequence;
    public Dictionary<(int Part,string Kind),long> StatusSequences=[];
    public bool AwaitingNativeClose=input.HoldForInput;
    public LogReason FailureReason;
    public string? RefusalCode;
    public string? NetworkError;public int? SocketError;
    public OutgoingView View=new(input.Id,input.Text,"",input.Channel,"queued",null,input.DisplayMode,"",config.OutgoingTargetLanguage,TranslationLanguages.Outgoing(config).Name);
}
public sealed class OutgoingService : IAsyncDisposable {
    private readonly object gate=new();private readonly Dictionary<string,OutgoingJob> jobs=new();
    private readonly Channel<OutgoingJob> queue=Channel.CreateBounded<OutgoingJob>(32);
    private CancellationTokenSource session=new();private readonly CancellationTokenSource stop=new();
    private readonly ConfigStore config;private readonly Func<string,Settings,bool,CancellationToken,Task<string>> translate;
    private readonly DiagnosticLog? log;
    private readonly Func<string,string,Settings,string[]>? context;
    private readonly Func<string,string[],Settings,bool,CancellationToken,Task<string>>? translateContext;
    private readonly Func<long> clock;
    private readonly Task[] workers;private readonly AsyncConcurrency slots;private string sessionId="";private long sequence;
    public OutgoingService(ConfigStore config,Func<string,Settings,bool,CancellationToken,Task<string>> translate,Func<long>? clock=null,DiagnosticLog? log=null,Func<string,string,Settings,string[]>? context=null,Func<string,string[],Settings,bool,CancellationToken,Task<string>>? translateContext=null){this.config=config;this.translate=translate;this.clock=clock??(()=>Environment.TickCount64);this.log=log;this.context=context;this.translateContext=translateContext;slots=new(config.Current.OutgoingConcurrency);config.Changed+=UpdateLimit;workers=Enumerable.Range(0,8).Select(_=>Run()).ToArray();}
    private void UpdateLimit()=>slots.SetLimit(config.Current.OutgoingConcurrency);
    private void Audit(OutgoingJob job,LogKind kind,LogReason reason=LogReason.None){if(kind==LogKind.SendFailed)job.FailureReason=reason;log?.Record("sending",kind,new(Reason:reason,Token:DiagnosticLog.Token(sessionId,job.Input.Id),Stage:job.View.State,Channel:job.Input.Channel,ElapsedMs:clock()-job.Created,SendLength:SendLengthEvidence.For(job.View)));}
    public OutgoingDiagnostics Diagnostics {get{lock(gate){ExpireLocked();var latest=jobs.Values.MaxBy(j=>j.Sequence);return latest==null?new("idle",null,null):new(latest.View.State,latest.View.State=="failed"?latest.FailureReason.ToString():null,latest.RefusalCode,latest.View.State=="failed"?latest.NetworkError:null,latest.View.State=="failed"?latest.SocketError:null);}}}
    public OutgoingView[] Recent {get{lock(gate){ExpireLocked();return OrderedViews().ToArray();}}}
    public object Stats {get{lock(gate)return new{pending=jobs.Values.Count(j=>j.View.State is not ("failed" or "submitted")),limit=config.Current.OutgoingQueueLimit,workers=slots.Stats};}}
    private IEnumerable<OutgoingView> OrderedViews(){
        var channels=new HashSet<string>();
        foreach(var job in jobs.Values.OrderBy(j=>j.Sequence)){
            var view=job.View;
            if(view.State is not ("failed" or "submitted")){
                var first=channels.Add(view.Channel);
                if(!first && view.State=="ready")view=view with{State="waiting_order",Error="译文已完成，等待同一频道前一条消息提交"};
            }
            yield return view;
        }
    }
    private void ExpireLocked(){
        foreach(var job in jobs.Values){
            if(clock()-job.Created<=(job.Config.OutgoingTimeoutSeconds+5)*1000)continue;
            if(job.View.State is "ready" or "waiting_input"){job.View=job.View with{State="failed",Error="任务已过期，剩余内容未自动发送；原文和译文已保留",WaitReason=null};Audit(job,LogKind.SendFailed,LogReason.Timeout);}
            else if(job.View.State=="waiting_channel"){job.View=job.View with{State="failed",Error="未能确认发送频道，未发送；原文已保留"};Audit(job,LogKind.SendFailed,LogReason.UnknownChannel);}
        }
    }
    public OutgoingView[] Exchange(string id,OutgoingInput[] input,string[] sent,string[]? waiting=null,OutgoingRefusal[]? refused=null,OutgoingPartAck[]? partAcks=null,QuickMessage[]? presets=null,bool multipartSupported=true,OutgoingStatus[]? statuses=null){
        if(input.Length>3 || sent.Length>12 || (waiting?.Length??0)>12 || (refused?.Length??0)>12 || input.Any(x=>x.Id.Length is <1 or >100 || x.Text.Length is <1 or >1024 || (x.Channel!="team" && x.Channel!="all" && x.Channel!="party") || (x.DisplayMode!="translation" && x.DisplayMode!="bilingual")))throw new ArgumentException("发送任务无效");
        var current=config.Current;
        if ((partAcks?.Length??0)>12 || (presets?.Length??0)>3) throw new ArgumentException("发送确认或快捷短语数量超限");
        if ((statuses?.Length??0)>24 || (statuses??[]).Any(x=>x.Seq<1 || x.Id.Length is <1 or >100 || x.Part is <0 or >7 || x.At<0 || x.Channel is not ("team" or "all" or "party") || x.State is not ("capture_ready" or "waiting_input" or "waiting_channel" or "ready" or "dispatched" or "echo_observed" or "echo_unobserved") || x.Reason is not (null or "" or "settings" or "input" or "channel" or "channel-fault" or "timeout" or "capacity" or "ambiguous" or "channel-mismatch"))) throw new ArgumentException("发送状态无效");
        var snapshots=input.Select(request=>current with{
            OutgoingTargetLanguage=request.TargetLanguage??current.OutgoingTargetLanguage,
            OutgoingCustomLanguage=request.CustomLanguage??current.OutgoingCustomLanguage,
            OutgoingCustomLanguageCode=request.CustomLanguageCode??current.OutgoingCustomLanguageCode,
            OutgoingSeparateBilingual=request.SeparateBilingual??current.OutgoingSeparateBilingual,
            OutgoingSplitLong=request.SplitLong??current.OutgoingSplitLong
        }).ToArray();
        foreach(var snapshot in snapshots)TranslationLanguages.Outgoing(snapshot);
        lock(gate){
            if(id!=sessionId){session.Cancel();session.Dispose();session=new();sessionId=id;while(queue.Reader.TryRead(out _)){}jobs.Clear();sequence=0;}
            ExpireLocked();
            foreach(var ack in sent)if(jobs.TryGetValue(ack,out var job) && !job.AwaitingNativeClose && job.View.PartCount==1 && job.View.State is "ready" or "waiting_channel" or "waiting_input"){job.View=job.View with{State="submitted",Error=null,WaitReason=null};Audit(job,LogKind.SendPhase);}
            var acknowledgedJobs=new HashSet<string>();
            foreach(var ack in partAcks??[])if(jobs.TryGetValue(ack.Id,out var job) && !job.AwaitingNativeClose && job.View.PartIndex==ack.Part && !acknowledgedJobs.Contains(ack.Id) && job.View.State is "ready" or "waiting_channel" or "waiting_input") {
                acknowledgedJobs.Add(ack.Id);
                var next=ack.Part+1;
                job.View=next<job.Parts.Length?job.View with{PartIndex=next,SendText=job.Parts[next],State="ready",Error=null,WaitReason=null}:job.View with{State="submitted",Error=null,WaitReason=null};
                Audit(job,LogKind.SendPhase);
            }
            // The game client refused to submit. Tell the player why instead of leaving the task
            // looking ready (for example a client that is newer than this backend).
            ExpireLocked(); // Free expired slots before considering a new request.
            foreach(var preset in presets??[]) {
                // Only a still-enabled saved shortcut may be admitted; no cloud call.
                if (preset.Channel is not ("team" or "all" or "party") || !current.QuickMessages.Any(q=>q.Enabled&&(q==preset||q.Channel=="current"&&(q with{Channel=preset.Channel})==preset))) continue;
                var request=new OutgoingInput("preset-"+Guid.NewGuid().ToString("N"),preset.Text,preset.Channel);
                var job=new OutgoingJob(request,session.Token,current){Created=clock(),Sequence=++sequence};
                jobs.Add(request.Id,job);
                try {
                    if(!current.OutgoingEnabled)throw new InvalidDataException("发送功能已关闭");
                    if(jobs.Values.Count(j=>j.View.State is not ("failed" or "submitted"))>current.OutgoingQueueLimit)throw new InvalidDataException("发送队列已满，快捷短语未发送");
                    var plan=OutgoingParts.Plan("",preset.Text,"translation",false,current.OutgoingSplitLong);job.Parts=plan.Parts;
                    job.View=job.View with{English=preset.Text,SendText=job.Parts[0],PartCount=job.Parts.Length,OmittedCharacters=plan.OmittedCharacters,IsPreset=true,State="ready"};
                    Audit(job,LogKind.SendQueued);
                } catch(InvalidDataException ex) {job.View=job.View with{IsPreset=true,State="failed",Error=ex.Message};Audit(job,LogKind.SendFailed,LogReason.InvalidData);}
            }
            for(var i=0;i<input.Length;i++){
                var request=input[i];
                if(jobs.ContainsKey(request.Id))continue;
                var job=new OutgoingJob(request,session.Token,snapshots[i]){Created=clock(),Sequence=++sequence,SupportsMultipart=multipartSupported,Context=snapshots[i].OutgoingUseContext?context?.Invoke(id,request.Channel,snapshots[i])??[]:[]};jobs.Add(request.Id,job);
                log?.Record("sending",LogKind.SendQueued,new(Token:DiagnosticLog.Token(id,request.Id),Channel:request.Channel,Count:jobs.Count));
                if(!config.Current.OutgoingEnabled)job.View=job.View with{State="failed",Error="发送翻译已关闭"};
                else if(jobs.Values.Count(j=>j.View.State is "queued" or "translating" or "ready" or "waiting_channel" or "waiting_input")>config.Current.OutgoingQueueLimit)job.View=job.View with{State="failed",Error="发送队列已满，原文保留在设置页"};
                else if(request.HoldForInput)job.View=job.View with{State="waiting_input",WaitReason="input",Error="已保留原文，等待原生输入框关闭后继续；超时后不会补发"};
                else if(!queue.Writer.TryWrite(job))job.View=job.View with{State="failed",Error="发送队列已满，原文保留在设置页"};
                if(job.View.State=="failed")Audit(job,LogKind.SendFailed,config.Current.OutgoingEnabled?LogReason.QueueFull:LogReason.Disabled);
            }
            // Refusal can accompany the first retained snapshot after a close failure.
            foreach(var item in refused??[])if(jobs.TryGetValue(item.Id,out var job) && job.View.State is not ("submitted" or "failed")){job.RefusalCode=item.Reason is "disabled" or "backend-outdated" or "invalid-send-text" or "input-truncated" or "channel-fault" or "submit-rejected" or "submit-error" or "submit-unconfirmed" or "native-close" or "timeout"?item.Reason:"other";job.View=job.View with{State="failed",Error=RefusalMessage(item.Reason),WaitReason=null};Audit(job,LogKind.SendFailed,item.Reason=="input-truncated"?LogReason.NativeInputTruncated:LogReason.Other);}
            foreach(var item in (statuses??[]).OrderBy(x=>x.Seq)) {
                if(!jobs.TryGetValue(item.Id,out var job) || item.Channel!=job.Input.Channel || item.Part>=job.View.PartCount)continue;
                var statusKey=(item.Part,item.State=="capture_ready"?"capture":item.State is "ready" or "waiting_input" or "waiting_channel"?"wait":item.State=="dispatched"?"dispatch":"echo");
                if(job.StatusSequences.TryGetValue(statusKey,out var seen) && item.Seq<=seen)continue;
                if(item.State=="capture_ready") {
                    if(!job.AwaitingNativeClose || item.Part!=0 || job.View.State is not ("waiting_input" or "waiting_channel"))continue;
                    job.AwaitingNativeClose=false;job.View=job.View with{State="queued",WaitReason=null,Error=null};
                    if(!queue.Writer.TryWrite(job)){job.View=job.View with{State="failed",Error="发送队列已满，原文已保留"};Audit(job,LogKind.SendFailed,LogReason.QueueFull);}
                } else if(item.State is "waiting_input" or "waiting_channel" or "ready") {
                    if(item.Part!=job.View.PartIndex || job.View.State is not ("ready" or "waiting_input" or "waiting_channel"))continue;
                    if(job.AwaitingNativeClose && item.State!="waiting_input")continue;
                    job.View=job.View with{State=item.State,WaitReason=item.State=="ready"?null:item.Reason,Error=item.State=="waiting_input"?item.Reason=="settings"?"设置窗口正在使用输入，关闭后自动继续；超时后保留内容，不再补发":"正在等待原生聊天输入框关闭；关闭后复查频道并自动继续，超时后不补发":item.State=="waiting_channel"?"正在等待频道确认；不会改发其他频道，超时后不补发":null};
                } else {
                    if(job.AwaitingNativeClose)continue;
                    var parts=job.View.PartStatuses?.ToArray()??Enumerable.Range(0,job.View.PartCount).Select(n=>new OutgoingPartStatus(n)).ToArray();
                    var previous=parts[item.Part];
                    if(item.State=="dispatched") {if(previous.SubmittedAt!=null)continue;parts[item.Part]=previous with{State="dispatched",SubmittedAt=item.At};}
                    else {if(previous.SubmittedAt==null || previous.State is "echo_observed" or "echo_unobserved")continue;parts[item.Part]=previous with{State=item.State,ObservedAt=item.State=="echo_observed"?item.At:null,Reason=item.Reason};}
                    job.View=job.View with{PartStatuses=parts};
                    log?.Record("sending",LogKind.SendPhase,new(Token:DiagnosticLog.Token(sessionId,item.Id),Stage:item.State,Channel:item.Channel,Count:item.Part));
                }
                // Only accepted transitions consume a sequence. Separate part/kind
                // counters preserve delayed observations from an earlier segment.
                job.StatusSequences[statusKey]=item.Seq;
            }
            // The game reports a channel it could not confirm. Keep the text and wait for the rest
            // of the deadline instead of downgrading the message to another channel.
            foreach(var pendingId in waiting??[])if(jobs.TryGetValue(pendingId,out var held) && held.View.State=="ready")held.View=held.View with{State="waiting_channel",Error=held.View.IsPreset?"快捷短语指定频道与当前游戏频道不一致；请在游戏聊天框选择该频道后关闭输入框，或把短语设为当前频道。超时后不会补发。":"未能确认发送频道，正在等待；不会改发全体"};
            while(jobs.Count>48){var key=jobs.FirstOrDefault(x=>x.Value.View.State is "failed" or "submitted").Key;if(key==null)break;jobs.Remove(key);}
            return OrderedViews().Where(j=>j.State!="submitted").ToArray();
        }
    }
    public static string RefusalMessage(string reason)=>reason switch{
        "disabled"=>"发送翻译已关闭，已取消待发送消息；原文已保留",
        "backend-outdated"=>"客户端与后台版本不一致，未发送；请同步更新后再试",
        "invalid-send-text"=>"最终文本不符合单行或长度要求，未发送；原文已保留",
        "input-truncated"=>"游戏输入框截短或改变了文本，已停止提交，避免发送残缺内容；原文和译文已保留。请开启超长内容自动分条，并成套更新后台与 VPK",
        "channel-fault"=>"检测到发送频道不一致，已暂停发送；原文已保留",
        "submit-rejected"=>"游戏未接受发送事件，未发送；原文已保留",
        "submit-unconfirmed"=>"游戏未确认读取待发送文字，无法确认发送；为避免重复没有重试，原文已保留",
        "native-close"=>"游戏聊天框未能收起，未发送；原文已保留。请按 Esc 关闭聊天后再试",
        "timeout"=>"任务已过期，剩余内容未发送；原文和译文已保留，不会自动补发",
        "submit-error"=>"游戏提交事件执行失败，未重复发送；原文已保留",
        _=>"游戏端未提交该消息，原文已保留"
    };
    public static string SafeMessage(string text){
        var value=CleanMessage(text);
        if(value.Length==0 || OutgoingParts.CharacterCount(value)>OutgoingParts.Limit)throw new InvalidDataException($"发送译文为空或超过游戏单条 {OutgoingParts.Limit} 字符上限，未发送");
        return value;
    }
    public static string CleanMessage(string text)=>Regex.Replace(NormalizeLine(text),@"[\x00-\x1f\x7f]+"," ").Replace('"','\'').Replace(';',',').Replace('\\','/').Trim();
    // Single line, plain text, identical whitespace rules on both sides of the bridge.
    public static string NormalizeLine(string? text)=>Regex.Replace(text??"",@"\s+"," ").Trim();
    // The display mode is a snapshot taken when the player pressed the hotkey; it is never
    // re-read from the user's settings while the task is queued. Both engines share this rule.
    public static string BuildSendText(string? original,string english,string displayMode){
        return OutgoingParts.Build(original??"",english,displayMode,false,false)[0];
    }
    private async Task Run(){try{await foreach(var job in queue.Reader.ReadAllAsync(stop.Token)){
        if(job.Token.IsCancellationRequested)continue;
        lock(gate){if(job.View.State=="failed")continue;}
        var settings=job.Config;using var ct=CancellationTokenSource.CreateLinkedTokenSource(stop.Token,job.Token);
        var remaining=settings.OutgoingTimeoutSeconds*1000-(clock()-job.Created);
        if(remaining<=0){lock(gate)if(job.View.State!="failed")job.View=job.View with{State="failed",Error="发送任务排队超时"};Audit(job,LogKind.SendFailed,LogReason.Timeout);continue;}
        ct.CancelAfter(TimeSpan.FromMilliseconds(remaining));
        string english;
        try{
            using var slot=await slots.Enter(ct.Token);
            ct.Token.ThrowIfCancellationRequested();
            lock(gate){if(job.View.State=="failed")continue;job.View=job.View with{State="translating"};}Audit(job,LogKind.SendPhase);
            try{english=translateContext==null?await translate(job.Input.Text,settings,false,ct.Token):await translateContext(job.Input.Text,job.Context,settings,false,ct.Token);}
            catch when(settings.OutgoingFallbackFast && !ct.IsCancellationRequested){english=await translate(job.Input.Text,settings,true,ct.Token);}
            ct.Token.ThrowIfCancellationRequested();
        }catch(OperationCanceledException){if(!job.Token.IsCancellationRequested){lock(gate)if(job.View.State!="failed")job.View=job.View with{State="failed",Error="发送翻译超时，原文保留在设置页"};Audit(job,LogKind.SendFailed,LogReason.Timeout);}continue;}
        catch(TranslationOutputException ex){if(!job.Token.IsCancellationRequested){lock(gate)if(job.View.State!="failed"){job.View=job.View with{State="failed",Error=ex.Message};job.FailureReason=ex.Reason;}log?.Record("sending",LogKind.SendFailed,new(Reason:ex.Reason,Token:DiagnosticLog.Token(sessionId,job.Input.Id),Provider:"quality",Stage:"llm-output",FailureType:nameof(TranslationOutputException)));}continue;}
        catch(InvalidOperationException ex){if(!job.Token.IsCancellationRequested){lock(gate)if(job.View.State!="failed")job.View=job.View with{State="failed",Error=ex.Message};Audit(job,LogKind.SendFailed,LogReason.Other);}continue;}
        catch(HttpRequestException ex){if(!job.Token.IsCancellationRequested){var providerFailure=ex as ProviderRequestException;var networkFailure=ex as ProviderNetworkException;var diagnosis=ApiConfigurationDiagnostics.NetworkFailure(ex);var reason=providerFailure?.Reason??(ex.StatusCode==System.Net.HttpStatusCode.TooManyRequests?LogReason.RateLimit:ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden?LogReason.Unauthorized:diagnosis.Reason);lock(gate)if(job.View.State!="failed"){job.NetworkError=ProviderNetworkFailure.Code(ex);job.SocketError=(int?)ProviderNetworkFailure.SocketCode(ex);job.View=job.View with{State="failed",Error=providerFailure?.Message??(networkFailure!=null?networkFailure.Message:diagnosis.Title+"："+diagnosis.Detail+" "+string.Join(" ",diagnosis.Steps))+" 原文已保留。"};job.FailureReason=reason;}log?.Record("sending",LogKind.SendFailed,new(Reason:reason,Token:DiagnosticLog.Token(sessionId,job.Input.Id),Provider:networkFailure!=null||providerFailure?.Provider=="llm"?"quality":providerFailure?.Provider,Stage:networkFailure?.Stage??providerFailure?.Stage,HttpStatus:(int?)ex.StatusCode,FailureType:ex.GetType().Name,NetworkError:ProviderNetworkFailure.Code(ex),SocketError:(int?)ProviderNetworkFailure.SocketCode(ex),ElapsedMs:clock()-job.Created));}continue;}
        catch{if(!job.Token.IsCancellationRequested){lock(gate)if(job.View.State!="failed")job.View=job.View with{State="failed",Error="发送翻译失败，原文保留在设置页"};Audit(job,LogKind.SendFailed,LogReason.Other);}continue;}
        if(job.Token.IsCancellationRequested)continue;
        string clean;
        try{clean=CleanMessage(OutgoingStyle.ApplyCase(english,settings));if(clean.Length is <1 or >2500)throw new InvalidDataException("发送译文为空或过长，未发送");}
        catch(InvalidDataException ex){lock(gate)if(job.View.State!="failed")job.View=job.View with{State="failed",Error=ex.Message};Audit(job,LogKind.SendFailed,LogReason.InvalidData);continue;}
        OutgoingPlan plan;
        try{plan=OutgoingParts.Plan(job.Input.Text,clean,job.Input.DisplayMode,settings.OutgoingSeparateBilingual,settings.OutgoingSplitLong);if(plan.Parts.Length>1&&!job.SupportsMultipart)throw new InvalidDataException("游戏 Mod 不支持分条发送，请成套更新后台和 VPK 后重试");}
        catch(InvalidDataException ex){lock(gate)if(job.View.State!="failed")job.View=job.View with{English=clean,State="failed",Error=ex.Message};Audit(job,LogKind.SendFailed,LogReason.InvalidData);continue;}
        lock(gate)if(!job.Token.IsCancellationRequested && job.View.State!="failed"){job.Parts=plan.Parts;job.View=job.View with{English=clean,SendText=plan.Parts[0],PartCount=plan.Parts.Length,OmittedCharacters=plan.OmittedCharacters,State="ready"};}
        Audit(job,LogKind.SendPhase);
    }}catch(OperationCanceledException){}}
    public async ValueTask DisposeAsync(){config.Changed-=UpdateLimit;stop.Cancel();session.Cancel();await Task.WhenAll(workers);stop.Dispose();session.Dispose();}
}
