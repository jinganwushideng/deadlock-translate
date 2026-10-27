using System.Diagnostics;

namespace DeadlockTranslate;
public sealed record LlmRequestView(string Id,DateTime At,string Direction,string Phase,int MaxTokens,bool ThinkingRequested,bool? ThinkingParameter,bool StreamingRequested,string Transport,int ContextCount,double ElapsedMs,int? InputTokens=null,int? OutputTokens=null,int? ReasoningTokens=null,string? FinishReason=null,double? FirstResponseMs=null,double? FirstContentMs=null);
public sealed class LlmTelemetry(DiagnosticLog? log=null) {
    private readonly DiagnosticLog? audit=log;
    private readonly object gate=new();private readonly Dictionary<string,Trace> items=new();
    public Trace Begin(Settings settings,bool outgoing,int contextCount,bool probe=false){lock(gate){while(items.Count>=64){var old=items.Values.FirstOrDefault(x=>x.Terminal)??items.Values.First();items.Remove(old.View.Id);}var trace=new Trace(this,new(Guid.NewGuid().ToString("N")[..16],DateTime.UtcNow,probe?"probe":outgoing?"outgoing":"incoming","queued",settings.LlmMaxTokens,settings.LlmThinkingEnabled,LlmRequestOptions.AppliedThinking(settings.LlmBaseUrl,settings.Model,settings.LlmThinkingApi,settings.LlmThinkingEnabled),settings.LlmStreaming,"pending",contextCount,0));items.Add(trace.View.Id,trace);return trace;}}
    public LlmRequestView[] Recent {get{lock(gate)return items.Values.Reverse().Take(12).Select(t=>t.Snapshot()).ToArray();}}
    public object Summary {get{lock(gate){var active=items.Values.Where(t=>!t.Terminal).ToArray();return new{waiting=active.Count(t=>t.View.Phase is "queued" or "waiting"),thinking=active.Count(t=>t.View.Phase=="thinking"),generating=active.Count(t=>t.View.Phase=="generating"),validating=active.Count(t=>t.View.Phase=="validating")};}}}
    public sealed class Trace {
        private readonly LlmTelemetry owner;private readonly long started=Stopwatch.GetTimestamp();internal LlmRequestView View;internal bool Terminal;
        internal Trace(LlmTelemetry owner,LlmRequestView view){this.owner=owner;View=view;}
        public string Id=>View.Id;
        public void Stage(string phase){lock(owner.gate){if(!Terminal)View=View with{Phase=phase is "queued" or "waiting" or "thinking" or "generating" or "validating"?phase:"waiting"};}}
        public void Response(string transport){lock(owner.gate){if(!Terminal)View=View with{Transport=transport=="stream"?"stream":"json",FirstResponseMs=View.FirstResponseMs??Stopwatch.GetElapsedTime(started).TotalMilliseconds};}}
        public void Content(){lock(owner.gate){if(!Terminal)View=View with{Phase="generating",FirstContentMs=View.FirstContentMs??Stopwatch.GetElapsedTime(started).TotalMilliseconds};}}
        public void Usage(int? input,int? output,int? reasoning,string? finish=null){lock(owner.gate){if(!Terminal)View=View with{InputTokens=input??View.InputTokens,OutputTokens=output??View.OutputTokens,ReasoningTokens=reasoning??View.ReasoningTokens,FinishReason=finish==null?View.FinishReason:ChatCompletionReader.SafeFinish(finish)};}}
        internal LlmRequestView Snapshot()=>Terminal?View:View with{ElapsedMs=Stopwatch.GetElapsedTime(started).TotalMilliseconds};
        public void Finish(string phase){lock(owner.gate){if(Terminal)return;Terminal=true;View=View with{Phase=phase is "done" or "cancelled"?phase:"failed",ElapsedMs=Stopwatch.GetElapsedTime(started).TotalMilliseconds};owner.audit?.Record("translation",LogKind.LlmUsage,new(Token:View.Id,Stage:"llm-usage",Provider:"quality",Count:View.ContextCount,ElapsedMs:View.ElapsedMs,Flag:View.Direction=="outgoing",InputTokens:View.InputTokens,OutputTokens:View.OutputTokens,ReasoningTokens:View.ReasoningTokens,MaxTokens:View.MaxTokens,FinishReason:View.FinishReason,FirstResponseMs:View.FirstResponseMs,FirstContentMs:View.FirstContentMs,Thinking:View.ThinkingParameter,Streaming:View.Transport=="stream"));}}
    }
}
