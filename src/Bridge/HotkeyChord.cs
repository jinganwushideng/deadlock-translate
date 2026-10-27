namespace DeadlockTranslate;

[Flags] public enum KeyModifiers { None=0,LeftControl=1,RightControl=2,LeftAlt=4,RightAlt=8,LeftShift=16,RightShift=32,LeftWindows=64,RightWindows=128 }
public sealed record HotkeyChord(int VirtualKey,KeyModifiers Control,KeyModifiers Alt,KeyModifiers Shift,string Canonical,string Label){
    public const string Default="LAlt+Space";
    public static readonly Dictionary<string,int> Keys=MakeKeys();
    private static Dictionary<string,int> MakeKeys(){var keys=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase){{"Space",32},{"Enter",13},{"Tab",9}};for(var i=0;i<26;i++)keys[((char)('A'+i)).ToString()]=65+i;for(var i=0;i<10;i++)keys[i.ToString()]=48+i;for(var i=1;i<=12;i++)keys["F"+i]=111+i;return keys;}
    public static bool TryParse(string? value,out HotkeyChord? result){
        result=null;if(string.IsNullOrWhiteSpace(value)||value.Length>64)return false;
        var pieces=value.Split('+',StringSplitOptions.TrimEntries);if(pieces.Length is <1 or >4||pieces.Any(string.IsNullOrEmpty))return false;
        KeyModifiers ctrl=0,alt=0,shift=0;var normalized=new List<string>();
        foreach(var p in pieces[..^1]){
            var upper=p.ToUpperInvariant();
            if(upper is "CTRL" or "CONTROL" or "LCTRL" or "RCTRL"){if(ctrl!=0)return false;ctrl=upper=="LCTRL"?KeyModifiers.LeftControl:upper=="RCTRL"?KeyModifiers.RightControl:KeyModifiers.LeftControl|KeyModifiers.RightControl;normalized.Add(upper=="LCTRL"?"LCtrl":upper=="RCTRL"?"RCtrl":"Ctrl");}
            else if(upper is "ALT" or "LALT" or "RALT"){if(alt!=0)return false;alt=upper=="LALT"?KeyModifiers.LeftAlt:upper=="RALT"?KeyModifiers.RightAlt:KeyModifiers.LeftAlt|KeyModifiers.RightAlt;normalized.Add(upper=="LALT"?"LAlt":upper=="RALT"?"RAlt":"Alt");}
            else if(upper is "SHIFT" or "LSHIFT" or "RSHIFT"){if(shift!=0)return false;shift=upper=="LSHIFT"?KeyModifiers.LeftShift:upper=="RSHIFT"?KeyModifiers.RightShift:KeyModifiers.LeftShift|KeyModifiers.RightShift;normalized.Add(upper=="LSHIFT"?"LShift":upper=="RSHIFT"?"RShift":"Shift");}
            else return false;
        }
        if(!Keys.TryGetValue(pieces[^1],out var vk))return false;
        if(ctrl==0&&alt==0&&shift==0&&(vk<112||vk>123))return false;
        if(alt!=0&&vk==115)return false; // Alt+F4 closes applications.
        var key=Keys.First(x=>x.Value==vk).Key;
        normalized=normalized.OrderBy(x=>x.Contains("Ctrl")?0:x.Contains("Alt")?1:2).ToList();normalized.Add(key);
        var labels=normalized.Select(x=>x switch{"LCtrl"=>"左 Ctrl","RCtrl"=>"右 Ctrl","LAlt"=>"左 Alt","RAlt"=>"右 Alt","LShift"=>"左 Shift","RShift"=>"右 Shift","Space"=>"空格",_=>x});
        result=new(vk,ctrl,alt,shift,string.Join('+',normalized),string.Join("＋",labels));return true;
    }
    public static HotkeyChord Parse(string value)=>TryParse(value,out var result)?result!:throw new ArgumentException("快捷键格式无效：使用修饰键＋字母/数字/空格/Enter/Tab，或 F1–F12；不能使用 Alt＋F4");
    public bool Matches(KeyModifiers held){
        if((held&(KeyModifiers.LeftWindows|KeyModifiers.RightWindows))!=0)return false;
        static bool Match(KeyModifiers selected,KeyModifiers actual)=>selected==0?actual==0:(selected&actual)!=0;
        return Match(Control,held&(KeyModifiers.LeftControl|KeyModifiers.RightControl))&&Match(Alt,held&(KeyModifiers.LeftAlt|KeyModifiers.RightAlt))&&Match(Shift,held&(KeyModifiers.LeftShift|KeyModifiers.RightShift));
    }
    public static KeyModifiers Modifier(int vk,int scan,int flags)=>vk switch{0xA2=>KeyModifiers.LeftControl,0xA3=>KeyModifiers.RightControl,0xA4=>KeyModifiers.LeftAlt,0xA5=>KeyModifiers.RightAlt,0xA0=>KeyModifiers.LeftShift,0xA1=>KeyModifiers.RightShift,0x5B=>KeyModifiers.LeftWindows,0x5C=>KeyModifiers.RightWindows,0x11=>(flags&1)!=0?KeyModifiers.RightControl:KeyModifiers.LeftControl,0x12=>(flags&1)!=0?KeyModifiers.RightAlt:KeyModifiers.LeftAlt,0x10=>scan==0x36?KeyModifiers.RightShift:KeyModifiers.LeftShift,_=>0};
}
