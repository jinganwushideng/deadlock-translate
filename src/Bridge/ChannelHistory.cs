namespace DeadlockTranslate;

// Short-lived channel text, in memory only. Sender identities are not part of the prompt.
public sealed class ChannelHistory(Func<long>? clock=null) {
    private readonly object gate=new();private readonly Func<long> now=clock??(()=>Environment.TickCount64);
    private readonly Dictionary<string,Queue<(long At,string Text)>> channels=new();private string session="";
    public void Reset(string id){lock(gate){if(session==id)return;session=id;channels.Clear();}}
    public void Add(string id,string channel,string text){lock(gate){if(id!=session||channel is not ("team" or "all" or "party"))return;if(!channels.TryGetValue(channel,out var q))channels[channel]=q=new();var at=now();q.Enqueue((at,text));while(q.Count>6||q.TryPeek(out var first)&&at-first.At>60000)q.Dequeue();}}
    public string[] Recent(string id,string channel,int count,int seconds){lock(gate){if(id!=session||!channels.TryGetValue(channel,out var q))return [];var at=now();return q.Where(x=>at-x.At<=seconds*1000).TakeLast(count).Select(x=>x.Text).ToArray();}}
}
