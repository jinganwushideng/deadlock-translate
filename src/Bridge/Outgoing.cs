using System.Threading.Channels;
using System.Text.RegularExpressions;

namespace DeadlockTranslate;
public record OutgoingInput(string Id,string Text,string Channel,string DisplayMode="translation");
public record OutgoingRefusal(string Id,string Reason);
public record OutgoingView(string Id,string Original,string English,string Channel,string State,string? Error,string DisplayMode="translation",string SendText="");
public sealed class OutgoingJob(OutgoingInput input,CancellationToken token) {
    public OutgoingInput Input=input;public CancellationToken Token=token;public long Created=Environment.TickCount64;
    public long Sequence;
    public OutgoingView View=new(input.Id,input.Text,"",input.Channel,"queued",null,input.DisplayMode,"");
}
public sealed class OutgoingService : IAsyncDisposable {
    private readonly object gate=new();private readonly Dictionary<string,OutgoingJob> jobs=new();
    private readonly Channel<OutgoingJob> queue=Channel.CreateBounded<OutgoingJob>(32);
    private CancellationTokenSource session=new();private readonly CancellationTokenSource stop=new();
    private readonly ConfigStore config;private readonly Func<string,Settings,bool,CancellationToken,Task<string>> translate;
    private readonly Func<long> clock;
    private readonly Task[] workers;private readonly AsyncConcurrency slots;private string sessionId="";private long sequence;
    public OutgoingService(ConfigStore config,Func<string,Settings,bool,CancellationToken,Task<string>> translate,Func<long>? clock=null){this.config=config;this.translate=translate;this.clock=clock??(()=>Environment.TickCount64);slots=new(config.Current.OutgoingConcurrency);config.Changed+=UpdateLimit;workers=Enumerable.Range(0,8).Select(_=>Run()).ToArray();}
    private void UpdateLimit()=>slots.SetLimit(config.Current.OutgoingConcurrency);
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
            if(clock()-job.Created<=(config.Current.OutgoingTimeoutSeconds+5)*1000)continue;
            if(job.View.State=="ready")job.View=job.View with{State="failed",Error="译文已过期，未自动发送"};
            else if(job.View.State=="waiting_channel")job.View=job.View with{State="failed",Error="未能确认发送频道，未发送；原文已保留"};
        }
    }
    public OutgoingView[] Exchange(string id,OutgoingInput[] input,string[] sent,string[]? waiting=null,OutgoingRefusal[]? refused=null){
        if(input.Length>3 || sent.Length>12 || (waiting?.Length??0)>12 || (refused?.Length??0)>12 || input.Any(x=>x.Id.Length is <1 or >100 || x.Text.Length is <1 or >1024 || (x.Channel!="team" && x.Channel!="all" && x.Channel!="party") || (x.DisplayMode!="translation" && x.DisplayMode!="bilingual")))throw new ArgumentException("发送任务无效");
        lock(gate){
            if(id!=sessionId){session.Cancel();session.Dispose();session=new();sessionId=id;while(queue.Reader.TryRead(out _)){}jobs.Clear();sequence=0;}
            foreach(var ack in sent)if(jobs.TryGetValue(ack,out var job) && job.View.State is "ready" or "waiting_channel")job.View=job.View with{State="submitted",Error=null};
            // The game client refused to submit. Tell the player why instead of leaving the task
            // looking ready (for example a client that is newer than this backend).
            foreach(var item in refused??[])if(jobs.TryGetValue(item.Id,out var job) && job.View.State is not ("submitted" or "failed"))job.View=job.View with{State="failed",Error=RefusalMessage(item.Reason)};
            ExpireLocked(); // Free expired slots before considering a new request.
            foreach(var request in input){
                if(jobs.ContainsKey(request.Id))continue;
                var job=new OutgoingJob(request,session.Token){Created=clock(),Sequence=++sequence};jobs.Add(request.Id,job);
                if(!config.Current.OutgoingEnabled)job.View=job.View with{State="failed",Error="发送翻译已关闭"};
                else if(jobs.Values.Count(j=>j.View.State is "queued" or "translating" or "ready" or "waiting_channel")>config.Current.OutgoingQueueLimit || !queue.Writer.TryWrite(job))job.View=job.View with{State="failed",Error="发送队列已满，原文保留在设置页"};
            }
            // The game reports a channel it could not confirm. Keep the text and wait for the rest
            // of the deadline instead of downgrading the message to another channel.
            foreach(var pendingId in waiting??[])if(jobs.TryGetValue(pendingId,out var held) && held.View.State=="ready")held.View=held.View with{State="waiting_channel",Error="未能确认发送频道，正在等待；不会改发全体"};
            while(jobs.Count>48){var key=jobs.FirstOrDefault(x=>x.Value.View.State is "failed" or "submitted").Key;if(key==null)break;jobs.Remove(key);}
            return OrderedViews().Where(j=>j.State!="submitted").ToArray();
        }
    }
    public static string RefusalMessage(string reason)=>reason switch{
        "disabled"=>"发送翻译已关闭，已取消待发送消息；原文已保留",
        "backend-outdated"=>"客户端与后台版本不一致，未发送；请同步更新后再试",
        "invalid-send-text"=>"最终文本不符合单行或长度要求，未发送；原文已保留",
        "channel-fault"=>"检测到发送频道不一致，已暂停发送；原文已保留",
        "submit-rejected"=>"游戏未接受发送事件，未发送；原文已保留",
        "submit-error"=>"游戏提交事件执行失败，未重复发送；原文已保留",
        _=>"游戏端未提交该消息，原文已保留"
    };
    public static string SafeMessage(string text){
        var value=Regex.Replace(NormalizeLine(text),@"[\x00-\x1f\x7f]+"," ").Replace('"','\'').Replace(';',',').Replace('\\','/').Trim();
        if(value.Length is <1 or >500)throw new InvalidDataException("英文译文为空或超过500字符，未发送");
        return value;
    }
    // Single line, plain text, identical whitespace rules on both sides of the bridge.
    public static string NormalizeLine(string? text)=>Regex.Replace(text??"",@"\s+"," ").Trim();
    // The display mode is a snapshot taken when the player pressed the hotkey; it is never
    // re-read from the user's settings while the task is queued. Both engines share this rule.
    public static string BuildSendText(string? original,string english,string displayMode){
        var clean=NormalizeLine(english);
        if(clean.Length==0)throw new InvalidDataException("英文译文为空或超过500字符，未发送");
        var source=NormalizeLine(original);
        var body=displayMode=="bilingual" && source.Length>0 && !string.Equals(source,clean,StringComparison.OrdinalIgnoreCase)
            ? source+" | "+clean
            : clean;
        if(body.Length>500)throw new InvalidDataException(displayMode=="bilingual"
            ? "组合消息过长，未发送，请缩短原文或改为仅英文"
            : "英文译文为空或超过500字符，未发送");
        try{return SafeMessage(body);}
        catch(InvalidDataException){throw new InvalidDataException(displayMode=="bilingual"
            ? "组合消息过长，未发送，请缩短原文或改为仅英文"
            : "英文译文为空或超过500字符，未发送");}
    }
    private async Task Run(){try{await foreach(var job in queue.Reader.ReadAllAsync(stop.Token)){
        if(job.Token.IsCancellationRequested)continue;
        lock(gate){if(job.View.State=="failed")continue;}
        var settings=config.Current;using var ct=CancellationTokenSource.CreateLinkedTokenSource(stop.Token,job.Token);
        var remaining=settings.OutgoingTimeoutSeconds*1000-(clock()-job.Created);
        if(remaining<=0){lock(gate)if(job.View.State!="failed")job.View=job.View with{State="failed",Error="发送任务排队超时"};continue;}
        ct.CancelAfter(TimeSpan.FromMilliseconds(remaining));
        string english;
        try{
            using var slot=await slots.Enter(ct.Token);
            ct.Token.ThrowIfCancellationRequested();
            lock(gate){if(job.View.State=="failed")continue;job.View=job.View with{State="translating"};}
            try{english=await translate(job.Input.Text,settings,false,ct.Token);}
            catch when(settings.OutgoingFallbackFast && !ct.IsCancellationRequested){english=await translate(job.Input.Text,settings,true,ct.Token);}
            ct.Token.ThrowIfCancellationRequested();
        }catch(OperationCanceledException){if(!job.Token.IsCancellationRequested)lock(gate)if(job.View.State!="failed")job.View=job.View with{State="failed",Error="发送翻译超时，原文保留在设置页"};continue;}
        catch(InvalidOperationException ex){if(!job.Token.IsCancellationRequested)lock(gate)if(job.View.State!="failed")job.View=job.View with{State="failed",Error=ex.Message};continue;}
        catch{if(!job.Token.IsCancellationRequested)lock(gate)if(job.View.State!="failed")job.View=job.View with{State="failed",Error="发送翻译失败，原文保留在设置页"};continue;}
        if(job.Token.IsCancellationRequested)continue;
        string clean;
        try{clean=SafeMessage(english);}
        catch(InvalidDataException ex){lock(gate)if(job.View.State!="failed")job.View=job.View with{State="failed",Error=ex.Message};continue;}
        string sendText;
        try{sendText=BuildSendText(job.Input.Text,clean,job.Input.DisplayMode);}
        catch(InvalidDataException ex){lock(gate)if(job.View.State!="failed")job.View=job.View with{English=clean,State="failed",Error=ex.Message};continue;}
        lock(gate)if(!job.Token.IsCancellationRequested && job.View.State!="failed")job.View=job.View with{English=clean,SendText=sendText,State="ready"};
    }}catch(OperationCanceledException){}}
    public async ValueTask DisposeAsync(){config.Changed-=UpdateLimit;stop.Cancel();session.Cancel();await Task.WhenAll(workers);stop.Dispose();session.Dispose();}
}
