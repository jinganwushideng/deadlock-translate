using System.Collections.Concurrent;

namespace DeadlockTranslate;

// Configuration is prepared outside the keyboard callback. The callback only updates
// small key states and queues an immutable, session-bound message (never sends it).
public sealed class QuickMessageHotkeys {
    sealed record Entry(QuickMessage Message,HotkeyChord Chord);
    Entry[] entries=[];
    readonly ConcurrentQueue<QuickMessagePress> pending=new();
    string session="",channel="";bool compatible;long lastTrigger=long.MinValue/2;
    KeyModifiers held;
    readonly bool[] pressed=new bool[256];
    int swallowed;
    public void Configure(Settings settings) {
        Volatile.Write(ref entries,settings.QuickMessages.Where(q=>q.Enabled).Select(q=>new Entry(q,HotkeyChord.Parse(q.Hotkey))).ToArray());
        while(pending.TryDequeue(out _)){}
    }
    public void Session(string value,bool supported,string currentChannel="") {Volatile.Write(ref channel,currentChannel is "team" or "all" or "party"?currentChannel:"");Volatile.Write(ref session,value);Volatile.Write(ref compatible,supported);}
    public void Seed(KeyModifiers modifiers){held=modifiers;}
    public bool HasSuppressedKey=>swallowed!=0;
    public void RecoverRelease(Func<int,bool> physicallyDown){for(var key=0;key<pressed.Length;key++)if(pressed[key]&&!physicallyDown(key)){pressed[key]=false;if(swallowed==key)swallowed=0;}}
    public bool Process(int key,int scan,int flags,bool down,bool up,long now,bool allowed) {
        if(key is <0 or >=256||(flags&0x10)!=0)return false;
        var modifier=HotkeyChord.Modifier(key,scan,flags);
        if(modifier!=0){if(down)held|=modifier;else if(up)held&=~modifier;return false;}
        if(key==swallowed){if(up){pressed[key]=false;swallowed=0;}return true;}
        if(up){pressed[key]=false;return false;}
        if(!down||pressed[key])return false;
        pressed[key]=true;
        var owner=Volatile.Read(ref session);var enabled=allowed&&Volatile.Read(ref compatible)&&owner.Length>0;
        if(!enabled)return false;
        foreach(var entry in Volatile.Read(ref entries)) {
            if(entry.Chord.VirtualKey!=key||!entry.Chord.Matches(held))continue;
            var target=entry.Message.Channel=="current"?Volatile.Read(ref channel):entry.Message.Channel;
            if(target is not ("team" or "all" or "party"))return false;
            swallowed=key;
            if(now-lastTrigger>=900&&pending.Count<3){lastTrigger=now;pending.Enqueue(new(owner,now,entry.Message,target));}
            return true;
        }
        return false;
    }
    public QuickMessage[] Take(string owner,long now) {
        var result=new List<QuickMessage>();
        while(pending.TryDequeue(out var item))if(item.Session==owner&&now>=item.At&&now-item.At<3000&&result.Count<3)result.Add(item.Message with{Channel=item.Channel});
        return result.ToArray();
    }
}
