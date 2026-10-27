using System.Security.Cryptography;

namespace DeadlockTranslate;

public sealed record NativeEditorDraft(string Owner,string Label,string Value,bool Multiline=false,int MaxLength=1024);
public sealed record NativeEditorCommand(string Id,string Action,string? Value=null,int Request=0);
public sealed record NativeEditorView(string Id,string State,string? Label=null,string? Value=null,bool Multiline=false,int MaxLength=1024,int Request=0,string? Message=null);

// Short-lived drafts only. Never saved, translated, logged, or exported. A browser
// page owns its result; a paired native surface can claim/complete an opaque ticket.
public sealed class NativeSettingsEditor : IDisposable {
    private sealed class Draft(string id,NativeEditorDraft input,long now) {
        public string Id=id,Owner=input.Owner,Label=input.Label,Value=input.Value,State="pending";
        public bool Multiline=input.Multiline;
        public int MaxLength=input.MaxLength;
        public long Until=now+15000;
    }
    private readonly object gate=new();
    private readonly Dictionary<string,Draft> drafts=new(StringComparer.Ordinal);
    private readonly Func<long> clock;
    private readonly Timer sweeper;
    public NativeSettingsEditor(Func<long>? monotonicClock=null) {
        clock=monotonicClock??(()=>Environment.TickCount64);
        sweeper=new(_=>{lock(gate)Prune();},null,15000,15000);
    }
    public static bool ValidId(string? value,int length=48)=>value?.Length==length&&value.All(c=>char.IsAsciiHexDigit(c)&&!char.IsUpper(c));
    private void Prune(){var now=clock();foreach(var id in drafts.Where(x=>x.Value.Until<=now).Select(x=>x.Key).ToArray())drafts.Remove(id);}
    private static void ValidateText(string? value,int limit,bool multiline) {
        if(value==null||value.Length>limit||value.Any(c=>c<' '&&c!='\t'&&!(multiline&&c is '\r' or '\n')))
            throw new ArgumentException("编辑内容过长或包含不支持的控制字符。");
    }
    public NativeEditorView Create(NativeEditorDraft input) {
        if(!ValidId(input.Owner,32)||string.IsNullOrWhiteSpace(input.Label)||input.Label.Length>80||input.MaxLength is <1 or >4096)
            throw new ArgumentException("原生编辑请求无效，请重新打开设置页。");
        ValidateText(input.Value,input.MaxLength,input.Multiline);ValidateText(input.Label,80,false);
        lock(gate){
            Prune();
            if(drafts.Values.Any(d=>d.Owner==input.Owner&&d.State is "pending" or "editing"))throw new InvalidOperationException("请先完成或取消当前编辑。");
            if(drafts.Count>=16)throw new InvalidOperationException("编辑窗口过多，请关闭其他设置页后重试。");
            var id=Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var draft=new Draft(id,input,clock());drafts.Add(id,draft);return new(id,"pending");
        }
    }
    public NativeEditorView? Read(string id,string owner) {
        if(!ValidId(id)||!ValidId(owner,32))return null;
        lock(gate){Prune();if(!drafts.TryGetValue(id,out var d)||d.Owner!=owner)return null;return new(d.Id,d.State,Value:d.State=="applied"?d.Value:null);}
    }
    public bool Cancel(string id,string owner) {
        if(!ValidId(id)||!ValidId(owner,32))return false;
        lock(gate){Prune();if(!drafts.TryGetValue(id,out var d)||d.Owner!=owner)return false;
            // DELETE acknowledges consumed results as well as cancelling pending work.
            d.Value="";d.Label="";d.State="cancelled";d.Until=clock()+30000;return true;
        }
    }
    public NativeEditorView? Exchange(NativeEditorCommand command) {
        if(!ValidId(command.Id)||command.Action is not ("claim" or "apply" or "cancel"))throw new ArgumentException("原生编辑命令无效。");
        lock(gate){
            Prune();if(!drafts.TryGetValue(command.Id,out var d))return null;
            if(command.Action=="claim"){
                if(d.State=="pending"){d.State="editing";d.Until=clock()+600000;}
                return d.State=="editing"?new(d.Id,d.State,d.Label,d.Value,d.Multiline,d.MaxLength):new(d.Id,d.State);
            }
            if(d.State is "applied" or "cancelled")return new(d.Id,d.State);
            if(command.Action=="cancel"){d.State="cancelled";d.Value="";d.Label="";d.Until=clock()+30000;return new(d.Id,d.State);}
            if(d.State!="editing")throw new InvalidOperationException("编辑窗口尚未接管此请求。");
            ValidateText(command.Value,d.MaxLength,d.Multiline);
            d.Value=command.Value!;d.Label="";d.State="applied";d.Until=clock()+30000;
            return new(d.Id,d.State);
        }
    }
    public NativeEditorView Dispatch(NativeEditorCommand command,bool allowed) {
        // Editing errors are returned inside a healthy game exchange; a bad/expired
        // editor must never interrupt chat reception or the main bridge heartbeat.
        var id=ValidId(command.Id)?command.Id:"";
        if(!allowed||command.Request is <1 or >1000000000)return new(id,"error",Request:command.Request,Message:"未确认当前设置窗口或构建，请成套更新后重新打开设置。");
        try{return Exchange(command) is {} value?value with{Request=command.Request}:new(id,"error",Request:command.Request,Message:"编辑请求已过期，请取消后重新打开。");}
        catch(Exception ex) when(ex is ArgumentException or InvalidOperationException){return new(id,"error",Request:command.Request,Message:ex.Message);}
    }
    public void Dispose(){sweeper.Dispose();lock(gate){drafts.Clear();}}
}
