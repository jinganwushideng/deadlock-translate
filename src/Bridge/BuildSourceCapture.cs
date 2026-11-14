using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DeadlockTranslate;

// Materialize clipboard handles before the game changes them. Retaining the OLE
// clipboard proxy itself can recurse when that proxy is placed back on the clipboard.
internal sealed class BuildSourceCapture : IDisposable {
    private readonly BlockingCollection<Action> commands=new();
    private readonly Thread thread;
    private Session? session;
    private readonly string directory;
    private sealed record StoredSource(string Account,LocalHeroBuild Plan);
    private readonly ConcurrentDictionary<string,StoredSource> sources=new(StringComparer.Ordinal);

    private sealed record ClipboardFormat(uint Format,IntPtr Data);
    private sealed record Session(string Token,uint Sequence,List<ClipboardFormat> Original,string Title,uint? Id,DateTime Expires,bool Manual);
    private IntPtr window;
    internal BuildSourceCapture(string state) {
        directory=Path.Combine(state,"build-sources-v2");
        thread=new Thread(Run){IsBackground=true,Name="Build source clipboard"};
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    private void Run() {
        window=CreateWindowEx(0,"STATIC","",0,0,0,0,0,new IntPtr(-3),IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        try {
            foreach(var action in commands.GetConsumingEnumerable())action();
        } finally { ReleaseSession(false); if(window!=IntPtr.Zero)DestroyWindow(window); }
    }
    private Task<object> Invoke(Func<object> action) {
        var result=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Add(()=>{try {result.SetResult(action());}catch(Exception ex){result.SetException(ex);}});
        return result.Task;
    }
    internal Task<object> Prepare(string title,uint? id) => Invoke(()=>{
        ReleaseSession(false);
        List<ClipboardFormat> original;
        var manual=false;
        try { original=CaptureOriginal(); }
        catch(InvalidOperationException) { original=[]; manual=true; }
        var token=Guid.NewGuid().ToString("N");
        session=new(token,GetClipboardSequenceNumber(),original,title,id,DateTime.UtcNow.AddSeconds(manual?120:30),manual);
        return new{capture=true,token,manual};
    });
    internal Task<object> Read(string token) => Invoke(()=>{
        if(session is not Session current||current.Token!=token)return new{error="capture-expired"};
        if(DateTime.UtcNow>current.Expires){ReleaseSession(false);return new{error="capture-expired"};}
        var sequence=GetClipboardSequenceNumber();
        if(sequence==current.Sequence)return new{pending=true};
        if(!OpenClipboard(IntPtr.Zero))return new{pending=true};
        string? encoded=null;
        try {
            var handle=GetClipboardData(13);
            var size=handle==IntPtr.Zero?0:GlobalSize(handle);
            if(size is >0 and <=3*1024*1024){
                var data=GlobalLock(handle);
                if(data!=IntPtr.Zero)try{encoded=Marshal.PtrToStringUni(data,checked((int)size/2))?.TrimEnd('\0');}finally{GlobalUnlock(handle);}
            }
        } finally {CloseClipboard();}
        var owned=false;
        try {
            if(encoded==null) return new{error="export-format"};
            var plan=LocalBuildCatalog.ParseExport(Convert.FromBase64String(encoded));
            if(current.Id is >0 && plan.Id!=current.Id)return new{error="selection-changed"};
            // Valve's export deliberately renames the build to a localized copy name.
            // Require exact known export formatting or the independently obtained native ID.
            var canonical=LocalBuildCatalog.Canonical(current.Title);
            var title=LocalBuildCatalog.Canonical(plan.Name);
            if(current.Id is not >0&&title!=canonical&&title!=LocalBuildCatalog.Canonical("["+current.Title+"]的副本")&&
                title!=LocalBuildCatalog.Canonical("Copy of "+current.Title))
                return new{error="selection-changed"};
            owned=true;
            plan=plan with{Name=current.Title,Texts=plan.Texts.Select(text=>text==plan.Name?current.Title:text).ToArray(),
                Fields=plan.Fields?.Select(field=>field.Kind=="title"?field with{Text=current.Title}:field).ToArray()};
            var key=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{plan.Id,plan.Hero,plan.Version,plan.Texts,plan.Groups})));
            plan=plan with{Fingerprint=key};
            Store(key,plan);
            var clipboard=ReleaseSession(owned,sequence);
            return new{source=key,clipboard,plan=new{id=plan.Id,hero=plan.Hero,version=plan.Version,name=plan.Name,fingerprint=key,groups=plan.Groups,total=plan.Texts.Length}};
        } catch(Exception ex) when(ex is not OutOfMemoryException) {return new{error=ex is IOException or UnauthorizedAccessException?"unavailable":"export-format"};}
        finally {ReleaseSession(owned,sequence);}
    });
    internal object AcceptLocal(LocalHeroBuild plan) {
        Store(plan.Fingerprint,plan);
        return new{source=plan.Fingerprint,plan=new{id=plan.Id,hero=plan.Hero,version=plan.Version,name=plan.Name,fingerprint=plan.Fingerprint,total=plan.Texts.Length}};
    }
    private void Store(string key,LocalHeroBuild plan) {
        var stored=new StoredSource(LocalBuildCatalog.LocateCurrentCache()??"",plan);
        sources[key]=stored;
        Directory.CreateDirectory(directory);
        ConfigStore.WriteAtomic(Path.Combine(directory,key+".json"),JsonSerializer.SerializeToUtf8Bytes(stored));
        foreach(var file in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(file=>file.LastWriteTimeUtc).Skip(32)){
            sources.TryRemove(Path.GetFileNameWithoutExtension(file.Name),out _);
            file.Delete();
        }
    }
    internal LocalHeroBuild? Find(string key) {
        if(key.Length!=64||!key.All(Uri.IsHexDigit))return null;
        // The UI cannot provide a file path. Only snapshots captured by this backend are accepted.
        var account=LocalBuildCatalog.LocateCurrentCache()??"";
        if(sources.TryGetValue(key,out var stored)&&stored.Account==account)return stored.Plan;
        try {
            var file=new FileInfo(Path.Combine(directory,key+".json"));
            if(!file.Exists||file.Length>8*1024*1024)return null;
            stored=JsonSerializer.Deserialize<StoredSource>(File.ReadAllBytes(file.FullName));
            if(stored?.Account!=account||stored.Plan.Fingerprint!=key||stored.Plan.Texts.Length>256)return null;
            sources[key]=stored;
            return stored.Plan;
        }catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException){return null;}
    }
    private string ReleaseSession(bool restore,uint sequence=0) {
        if(session is not Session value)return "unchanged";
        session=null;
        try {
            if(value.Manual)return "manual-export";
            if(restore&&GetClipboardSequenceNumber()==sequence){
                for(var i=0;i<60;i++){
                    if(GetClipboardSequenceNumber()!=sequence)return "changed-by-user";
                    if(OpenClipboard(window)){
                        try {
                            if(GetClipboardSequenceNumber()!=sequence)return "changed-by-user";
                            if(!EmptyClipboard())return "restore-failed";
                            var complete=true;
                            for(var index=0;index<value.Original.Count;index++){
                                var format=value.Original[index];
                                if(SetClipboardData(format.Format,format.Data)!=IntPtr.Zero)
                                    value.Original[index]=format with{Data=IntPtr.Zero};
                                else complete=false;
                            }
                            return complete?"restored":"restore-incomplete";
                        } finally {CloseClipboard();}
                    }
                    Thread.Sleep(50);
                }
                return "clipboard-busy";
            }
            return restore?"changed-by-user":"unchanged";
        }finally{FreeFormats(value.Original);}
    }
    private List<ClipboardFormat> CaptureOriginal() {
        if(window==IntPtr.Zero||!OpenClipboard(window))throw new InvalidOperationException("Clipboard is busy");
        var result=new List<ClipboardFormat>();
        try {
            uint format=0;
            long size=0;
            while((format=EnumClipboardFormats(format))!=0){
                if(result.Count>=128||format==0x80||format is >=0x200 and <=0x2ff)
                    throw new InvalidOperationException("Clipboard format cannot be safely retained");
                var original=GetClipboardData(format);
                size+=(long)GlobalSize(original);
                if(original==IntPtr.Zero||size>64*1024*1024)throw new InvalidOperationException("Clipboard cannot be retained");
                var duplicate=OleDuplicateData(original,(ushort)format,0);
                if(duplicate==IntPtr.Zero)throw new InvalidOperationException("Clipboard duplication failed");
                result.Add(new(format,duplicate));
            }
            return result;
        }catch {FreeFormats(result);throw;}
        finally {CloseClipboard();}
    }
    private static void FreeFormats(List<ClipboardFormat> formats) {
        foreach(var format in formats){
            if(format.Data==IntPtr.Zero)continue;
            var medium=new System.Runtime.InteropServices.ComTypes.STGMEDIUM {
                tymed=format.Format is 2 or 9 or 0x82?System.Runtime.InteropServices.ComTypes.TYMED.TYMED_GDI:
                    format.Format is 3 or 0x83?System.Runtime.InteropServices.ComTypes.TYMED.TYMED_MFPICT:
                    format.Format is 14 or 0x8e?System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ENHMF:
                    System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL,
                unionmember=format.Data
            };
            ReleaseStgMedium(ref medium);
        }
    }
    public void Dispose(){commands.CompleteAdding();thread.Join(4000);}
    [DllImport("ole32.dll")] private static extern IntPtr OleDuplicateData(IntPtr source,ushort format,uint flags);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium);
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint previous);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint extended,string name,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr p);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint f);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(IntPtr h);
}
