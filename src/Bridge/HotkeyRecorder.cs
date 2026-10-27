namespace DeadlockTranslate;

public sealed record HotkeyRecordingView(string Id,string State,string? Binding=null,string? Label=null,string? Error=null,int SecondsRemaining=0);
public readonly record struct RecordingDecision(bool Suppress,bool BlockSending);

// Recording is explicit, window-scoped and short-lived. No key history is stored.
// Begin/status/cancel run on HTTP threads; held/debt state is owned by the existing hook thread.
public sealed class HotkeyRecorder(Action<HotkeyRecordingView>? finished=null) {
    private sealed class Session(string id,long now,nint window,bool game) {
        public readonly string Id=id;public readonly long Until=now+15000;public readonly nint Window=window;public readonly bool Game=game;
        public long Heartbeat=now;public KeyModifiers Held;
        public HotkeyRecordingView View=new(id,"arming",SecondsRemaining:15);
    }
    private Session? current;
    private readonly HashSet<int> suppressed=[];private int debtCount;
    public bool HasSuppressedKeys=>Volatile.Read(ref debtCount)>0;
    public string Begin(long now,nint window,bool game){
        var next=new Session(Guid.NewGuid().ToString("N"),now,window,game);
        var old=Interlocked.Exchange(ref current,next);if(old!=null)Finish(old,"cancelled");return next.Id;
    }
    private static bool Pending(Session s)=>Volatile.Read(ref s.View).State is "arming" or "recording";
    private void Finish(Session s,string state,string? error=null,string? binding=null,string? label=null){
        var old=Volatile.Read(ref s.View);
        while(old.State is "arming" or "recording"){
            var value=new HotkeyRecordingView(s.Id,state,binding,label,error);
            var observed=Interlocked.CompareExchange(ref s.View,value,old);if(ReferenceEquals(observed,old)){try{finished?.Invoke(value);}catch{}return;}old=observed;
        }
    }
    private bool Alive(Session s,long now){
        if(!Pending(s))return false;
        if(now>=s.Until||now-Interlocked.Read(ref s.Heartbeat)>=1500){Finish(s,"expired","录制超时或页面已关闭，请重新点击录制。");return false;}return true;
    }
    public bool IsArming(long now)=>Volatile.Read(ref current) is {} s&&Alive(s,now)&&Volatile.Read(ref s.View).State=="arming";
    public bool Active(long now)=>Volatile.Read(ref current) is {} s&&Alive(s,now);
    public bool Activate(long now,nint window,bool gameSettingsOpen){
        var s=Volatile.Read(ref current);if(s==null||!Alive(s,now)||Volatile.Read(ref s.View).State!="arming")return false;
        if(window!=s.Window||(s.Game&&!gameSettingsOpen)){Finish(s,"cancelled","设置窗口已失去焦点，请重新录制。");return false;}
        var old=Volatile.Read(ref s.View);return ReferenceEquals(Interlocked.CompareExchange(ref s.View,new(s.Id,"recording",SecondsRemaining:(int)Math.Ceiling((s.Until-now)/1000d)),old),old);
    }
    public HotkeyRecordingView? Status(string id,long now,bool heartbeat=true){
        var s=Volatile.Read(ref current);if(s==null||s.Id!=id)return null;
        if(Alive(s,now)&&heartbeat)Interlocked.Exchange(ref s.Heartbeat,now);
        var view=Volatile.Read(ref s.View);return view with{SecondsRemaining=Pending(s)?(int)Math.Max(0,Math.Ceiling((s.Until-now)/1000d)):0};
    }
    public void Cancel(string? id=null,bool gameOnly=false){var s=Volatile.Read(ref current);if(s!=null&&(id==null||s.Id==id)&&(!gameOnly||s.Game))Finish(s,"cancelled");}
    public void Fail(string error){var s=Volatile.Read(ref current);if(s!=null)Finish(s,"failed",error);}
    // Only called on the hook thread; after cancellation, consume the releases of keys
    // whose downs we owned, so a stray Alt-up cannot activate a browser/system menu.
    public void RecoverReleases(Func<int,bool> down){if(suppressed.Count==0)return;foreach(var key in suppressed.ToArray())if(!down(key))suppressed.Remove(key);Volatile.Write(ref debtCount,suppressed.Count);}
    public RecordingDecision Process(int key,int scan,int flags,bool down,bool up,long now,nint window,bool gameSettingsOpen){
        var s=Volatile.Read(ref current);var live=s!=null&&Alive(s,now);
        var modifier=HotkeyChord.Modifier(key,scan,flags);
        var debtKey=modifier switch{KeyModifiers.LeftControl=>0xA2,KeyModifiers.RightControl=>0xA3,KeyModifiers.LeftAlt=>0xA4,KeyModifiers.RightAlt=>0xA5,KeyModifiers.LeftShift=>0xA0,KeyModifiers.RightShift=>0xA1,_=>key};
        if(suppressed.Contains(debtKey)){
            if(up){suppressed.Remove(debtKey);Volatile.Write(ref debtCount,suppressed.Count);}
            if(live&&up)s!.Held&=~modifier;
            return new(true,live);
        }
        if(!live)return default;
        if(window!=s!.Window||(s.Game&&!gameSettingsOpen)){Finish(s,"cancelled","设置窗口已关闭或失去焦点，请重新录制。");return default;}
        if(Volatile.Read(ref s.View).State!="recording")return new(false,true);
        if(modifier!=0){if(down)s.Held|=modifier;else if(up)s.Held&=~modifier;}
        if(down){suppressed.Add(debtKey);Volatile.Write(ref debtCount,suppressed.Count);}
        if(!down)return new(false,true);
        if(key==27){Finish(s,"cancelled");return new(true,true);}
        if(modifier!=0)return new(true,true);
        var name=HotkeyChord.Keys.FirstOrDefault(p=>p.Value==key).Key;
        var parts=new List<string>();
        static string Side(KeyModifiers held,KeyModifiers left,KeyModifiers right,string name)=>held==(left|right)?name:held==left?"L"+name:"R"+name;
        foreach(var (left,right,label) in new[]{(KeyModifiers.LeftControl,KeyModifiers.RightControl,"Ctrl"),(KeyModifiers.LeftAlt,KeyModifiers.RightAlt,"Alt"),(KeyModifiers.LeftShift,KeyModifiers.RightShift,"Shift")}){
            var held=s.Held&(left|right);if(held!=0)parts.Add(Side(held,left,right,label));
        }
        if(name!=null)parts.Add(name);
        if(name!=null&&(s.Held&(KeyModifiers.LeftWindows|KeyModifiers.RightWindows))==0&&HotkeyChord.TryParse(string.Join('+',parts),out var chord))Finish(s,"completed",binding:chord!.Canonical,label:chord.Label);
        else{
            var old=Volatile.Read(ref s.View);if(old.State=="recording")Interlocked.CompareExchange(ref s.View,old with{Error="不支持这个组合；请选择修饰键＋字母/数字/空格/Enter/Tab，或 F1–F12，不能使用 Alt＋F4。"},old);
        }
        return new(true,true);
    }
}
