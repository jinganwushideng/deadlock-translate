using System.Diagnostics;

namespace DeadlockTranslate;
public sealed partial class Translators {
    private string? TimingToken()=>log==null?null:Guid.NewGuid().ToString("N")[..16];
    private void Timing(string stage,long started,int count,bool outgoing,string? token)=>log?.Record("translation",LogKind.TranslationTiming,new(Token:token,Stage:stage,Provider:"quality",Count:count,ElapsedMs:Stopwatch.GetElapsedTime(started).TotalMilliseconds,Flag:outgoing));
    private async Task<IDisposable> EnterQualityTimed(CancellationToken ct,int promptCharacters,bool outgoing,string? token,string? model=null){
        var started=Stopwatch.GetTimestamp();try{return await EnterProvider("llm",ct,outgoing,model);}finally{Timing("provider-queue",started,promptCharacters,outgoing,token);}
    }
}
