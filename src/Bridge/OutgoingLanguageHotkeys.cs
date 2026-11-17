namespace DeadlockTranslate;

public sealed record OutgoingLanguageBinding(string Id,string Language,string Hotkey,string Name="",string Code="",bool Enabled=true);
public sealed record OutgoingLanguageSelection(string Language,string Name,string Code,string Label,long Revision);
public sealed record SendHotkeyEvent(long Seq,long At,OutgoingLanguageSelection Language);

public sealed class OutgoingLanguageHotkeys {
    private sealed record Binding(OutgoingLanguageBinding Value,HotkeyChord Chord);
    private readonly object gate=new();
    private readonly Queue<SendHotkeyEvent> sends=new();
    private Binding[] bindings=[];
    private OutgoingLanguageSelection fallback=new("en","","","英语",0);
    private Binding? active;
    private string session="";
    private long revision;
    private int swallowed;

    public static void Validate(Settings settings){
        var list=settings.OutgoingLanguageBindings;
        if(list==null||list.Length>24)throw new ArgumentException("发送语言快捷键最多 24 条");
        var ids=new HashSet<string>(StringComparer.Ordinal);
        var languages=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var used=settings.QuickMessages.Where(q=>q.Enabled).Select(q=>HotkeyChord.Parse(q.Hotkey)).ToList();
        used.Add(HotkeyChord.Parse(settings.OutgoingHotkey));
        foreach(var entry in list){
            if(entry==null||string.IsNullOrWhiteSpace(entry.Id)||entry.Id.Length>40||entry.Id.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-')||!ids.Add(entry.Id))
                throw new ArgumentException("发送语言快捷键编号不能为空或重复");
            var lang=TranslationLanguages.Resolve(entry.Language,entry.Name,entry.Code);
            if(!languages.Add(lang.Id=="custom"?"custom:"+lang.Name+":"+lang.ServiceCode:lang.Id))
                throw new ArgumentException("同一种发送语言只能设置一个快捷键");
            var chord=HotkeyChord.Parse(entry.Hotkey);
            if(!entry.Enabled)continue;
            if(used.Any(other=>QuickMessages.Overlap(other,chord)))
                throw new ArgumentException($"「{lang.Name}」的发送语言快捷键与另一快捷键冲突");
            used.Add(chord);
        }
    }

    public void Configure(Settings settings){
        var defaultLanguage=TranslationLanguages.Outgoing(settings);
        var next=settings.OutgoingLanguageBindings.Where(x=>x.Enabled).Select(x=>new Binding(x,HotkeyChord.Parse(x.Hotkey))).ToArray();
        lock(gate){
            var configured=new OutgoingLanguageSelection(settings.OutgoingTargetLanguage,settings.OutgoingCustomLanguage,settings.OutgoingCustomLanguageCode,defaultLanguage.Name,0);
            if(active==null&&configured!=fallback)revision++;
            fallback=configured;
            if(active!=null){
                var preserved=next.FirstOrDefault(x=>x.Value==active.Value);
                if(preserved==null)revision++;
                active=preserved;
            }
            bindings=next;
        }
    }

    private OutgoingLanguageSelection CurrentLocked()=>active is {Value:var binding}
        ? new(binding.Language,binding.Name,binding.Code,TranslationLanguages.Resolve(binding.Language,binding.Name,binding.Code).Name,revision)
        : fallback with{Revision=revision};
    public OutgoingLanguageSelection Current {get {lock(gate)return CurrentLocked();}}
    public OutgoingLanguageSelection Reset(){lock(gate){if(active!=null){active=null;revision++;}return CurrentLocked();}}

    public bool Process(int key,int flags,bool down,bool up,KeyModifiers held,bool allowed){
        if((flags&0x10)!=0)return false;
        lock(gate){
            if(key==swallowed && key!=0){if(up)swallowed=0;return true;}
            if(!down||!allowed||swallowed!=0)return false;
            var selected=Array.Find(bindings,x=>x.Chord.VirtualKey==key&&x.Chord.Matches(held));
            if(selected==null)return false;
            swallowed=key;
            if(selected!=active){active=selected;revision++;}
            return true;
        }
    }

    public void RecoverRelease(Func<int,bool> down){lock(gate){if(swallowed!=0&&!down(swallowed))swallowed=0;}}
    public bool HasSuppressedKey {get {lock(gate)return swallowed!=0;}}
    public void Session(string value){lock(gate){if(value!=session){session=value;sends.Clear();}}}
    public void Capture(long seq,long at){
        lock(gate){
            if(session.Length==0)return;
            while(sends.TryPeek(out var old)&&at-old.At>=5000)sends.Dequeue();
            if(sends.Count<32)sends.Enqueue(new(seq,at,CurrentLocked()));
        }
    }
    public SendHotkeyEvent[] Pending(string owner,long seen,long now){
        lock(gate){
            if(owner!=session)return [];
            while(sends.TryPeek(out var item)&&(item.Seq<=seen||now-item.At>=5000))sends.Dequeue();
            return sends.Take(8).ToArray();
        }
    }
}
