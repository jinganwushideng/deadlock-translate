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
    private readonly string resetFile;
    private readonly object resetGate=new();
    private readonly HashSet<string> resetScopes=new(StringComparer.Ordinal);
    private sealed record StoredSource(string Account,LocalHeroBuild Plan);
    private readonly ConcurrentDictionary<string,StoredSource> sources=new(StringComparer.Ordinal);

    internal enum ClipboardKind { Unsupported, Internal, Memory, Bitmap }
    private sealed record ClipboardFormat(uint Format,IntPtr Data,ClipboardKind Kind);
    private sealed record Session(string Token,uint Sequence,List<ClipboardFormat> Original,string Title,uint? Id,uint? Version,DateTime Expires,bool Manual);
    private IntPtr window;
    internal BuildSourceCapture(string state) {
        directory=Path.Combine(state,"build-sources-v2");
        resetFile=Path.Combine(state,"build-source-cache-resets-v1.json");
        try {
            var info=new FileInfo(resetFile);
            if(info.Exists&&info.Length<65536)
                foreach(var key in JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(resetFile))??[])
                    if(key.Length==64&&key.All(Uri.IsHexDigit)&&resetScopes.Count<256)resetScopes.Add(key);
        }catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException){}
        ReviewSnapshots();
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
    internal Task<object> Prepare(string title,uint? id,uint? version=null) => Invoke(()=>{
        if(session is Session previous&&!previous.Manual){
            ReadCore(previous.Token);
            // Finish restoring an earlier automatic export before saving the next
            // clipboard. Otherwise rapid A-B switching can retain export A as user data.
            if(session!=null&&DateTime.UtcNow<previous.Expires.AddSeconds(-29))return new{pending=true};
        }
        ReleaseSession(false);
        List<ClipboardFormat> original;
        var manual=false;
        try { original=CaptureOriginal(); }
        catch(InvalidOperationException) { original=[]; manual=true; }
        var token=Guid.NewGuid().ToString("N");
        session=new(token,GetClipboardSequenceNumber(),original,title,id,version,DateTime.UtcNow.AddSeconds(manual?120:30),manual);
        return new{capture=true,token,manual};
    });
    internal Task<object> Read(string token) => Invoke(()=>ReadCore(token));
    private object ReadCore(string token) {
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
            // A matching native ID does not prove that a reused label has updated.
            // Validate the actual exported title before replacing its copy suffix.
            owned=current.Id is >0;
            if(!MatchesExportTitle(plan.Name,current.Title))return new{error="selection-changed"};
            owned=true;
            if(current.Version is >0 && plan.Version!=current.Version)return new{error="selection-changed"};
            plan=plan with{Name=current.Title,Texts=plan.Texts.Select(text=>text==plan.Name?current.Title:text).ToArray(),
                Fields=plan.Fields?.Select(field=>field.Kind=="title"?field with{Text=current.Title}:field).ToArray()};
            var key=SourceFingerprint(plan);
            plan=plan with{Fingerprint=key};
            Store(key,plan);
            var clipboard=ReleaseSession(owned,sequence);
            return new{source=key,clipboard,plan=new{id=plan.Id,hero=plan.Hero,version=plan.Version,name=plan.Name,fingerprint=key,total=plan.Texts.Length,isChinese=BuildTextJobs.IsChinesePlan(plan)}};
        } catch(Exception ex) when(ex is not OutOfMemoryException) {return new{error=ex is IOException or UnauthorizedAccessException?"unavailable":"export-format"};}
        finally {ReleaseSession(owned,sequence);}
    }
    internal object AcceptLocal(LocalHeroBuild plan) {
        if(!IsConsistent(plan))throw new InvalidDataException("Build source does not match native data");
        plan=plan with{Fingerprint=SourceFingerprint(plan)};
        Store(plan.Fingerprint,plan);
        return new{source=plan.Fingerprint,plan=new{id=plan.Id,hero=plan.Hero,version=plan.Version,name=plan.Name,fingerprint=plan.Fingerprint,total=plan.Texts.Length,isChinese=BuildTextJobs.IsChinesePlan(plan)}};
    }
    internal static bool MatchesExportTitle(string nativeTitle,string expected) {
        static string Plain(string value) => System.Text.RegularExpressions.Regex.Replace(
            System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(value,@"<[^>]*>","",System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))),
            @"\s+"," ",System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)).Trim();
        var actual=Plain(nativeTitle);
        return new[]{expected,"["+expected+"]的副本","Copy of "+expected}.Any(value=>actual.Equals(Plain(value),StringComparison.OrdinalIgnoreCase));
    }
    internal static bool IsConsistent(LocalHeroBuild plan) {
        try {
            if(plan.Serialized is not {Length:>0})return false;
            var native=LocalBuildCatalog.ParseSerialized(plan.Serialized);
            if(native.Id!=plan.Id||native.Hero!=plan.Hero||native.Version!=plan.Version||!MatchesExportTitle(native.Name,plan.Name))return false;
            var texts=native.Texts.Select(text=>text==native.Name?plan.Name:text).Order(StringComparer.Ordinal);
            return texts.SequenceEqual(plan.Texts.Order(StringComparer.Ordinal))&&native.Description==plan.Description&&native.Groups.SequenceEqual(plan.Groups);
        }catch(Exception ex) when(ex is not OutOfMemoryException){return false;}
    }
    private static string ScopeKey(string account,LocalHeroBuild plan) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(account+":"+plan.Id+":"+plan.Hero)));
    private void ResetScope(StoredSource source) {
        lock(resetGate){
            if(resetScopes.Count>=256||!resetScopes.Add(ScopeKey(source.Account,source.Plan)))return;
            Directory.CreateDirectory(Path.GetDirectoryName(resetFile)!);
            ConfigStore.WriteAtomic(resetFile,JsonSerializer.SerializeToUtf8Bytes(resetScopes));
        }
    }
    private void ReviewSnapshots() {
        if(!Directory.Exists(directory))return;
        foreach(var file in new DirectoryInfo(directory).EnumerateFiles("*.json").Take(64)){
            try {
                if(file.Length>8*1024*1024)continue;
                var stored=JsonSerializer.Deserialize<StoredSource>(File.ReadAllBytes(file.FullName));
                if(stored?.Plan!=null&&!IsConsistent(stored.Plan))ResetScope(stored);
            }catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException){}
        }
    }
    internal bool NeedsCacheRefresh(LocalHeroBuild plan) {lock(resetGate)return resetScopes.Contains(ScopeKey(LocalBuildCatalog.LocateCurrentCache()??"",plan));}
    internal static string SourceFingerprint(LocalHeroBuild plan) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new{schema=2,plan.Id,plan.Hero,plan.Version,plan.Name,plan.Description,plan.Groups,
            texts=plan.Texts.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            fields=plan.Fields?.Where(field=>field.Text.Length>0).OrderBy(field=>field.Id,StringComparer.Ordinal).ThenBy(field=>field.ItemId)})));
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
            if(stored?.Account!=account||stored.Plan==null||stored.Plan.Fingerprint!=key||stored.Plan.Texts is not {Length:<=256})return null;
            if(!IsConsistent(stored.Plan)){ResetScope(stored);return null;}
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
            uint currentFormat=0;
            var available=new List<uint>();
            while((currentFormat=EnumClipboardFormats(currentFormat))!=0){
                if(available.Count>=128)throw new InvalidOperationException("Too many clipboard formats");
                available.Add(currentFormat);
            }
            // Windows can synthesize bitmap/DIB views from DIBV5. Retaining all
            // three wastes tens of megabytes for one screenshot and forces fallback.
            var image=available.Contains(17)?17u:available.Contains(8)?8u:0u;
            long size=0;
            foreach(var format in available){
                if(image!=0&&format is 2 or 8 or 9 or 17&&format!=image)continue;
                var name=new StringBuilder(256);
                if(format>=0xc000)GetClipboardFormatName(format,name,name.Capacity);
                var kind=ClassifyClipboardFormat(format,name.ToString());
                if(kind==ClipboardKind.Internal)continue;
                if(kind==ClipboardKind.Unsupported)throw new InvalidOperationException("Clipboard format cannot be safely retained");
                var original=GetClipboardData(format);
                if(original==IntPtr.Zero)throw new InvalidOperationException("Clipboard cannot be retained");
                IntPtr duplicate;
                if(kind==ClipboardKind.Bitmap){
                    // HBITMAP/HPALETTE/OLE metadata are not HGLOBAL. Passing them
                    // to GlobalSize can fail fast with heap corruption on Windows.
                    if(GetObject(original,Marshal.SizeOf<NativeBitmap>(),out var bitmap)==0)throw new InvalidOperationException("Invalid bitmap");
                    size+=checked((long)bitmap.WidthBytes*Math.Abs((long)bitmap.Height));
                    if(size>64*1024*1024)throw new InvalidOperationException("Clipboard bitmap too large");
                    duplicate=CopyImage(original,0,0,0,0x2000);
                }else{
                    var bytes=GlobalSize(original);
                    size+=checked((long)bytes);
                    if(bytes==0||size>64*1024*1024)throw new InvalidOperationException("Clipboard memory too large");
                    duplicate=GlobalAlloc(2,bytes);
                    if(duplicate!=IntPtr.Zero){
                        var source=GlobalLock(original);var target=GlobalLock(duplicate);
                        if(source==IntPtr.Zero||target==IntPtr.Zero){
                            if(source!=IntPtr.Zero)GlobalUnlock(original);
                            if(target!=IntPtr.Zero)GlobalUnlock(duplicate);
                            GlobalFree(duplicate);throw new InvalidOperationException("Clipboard memory cannot be locked");
                        }
                        try{CopyMemory(target,source,bytes);}finally{GlobalUnlock(duplicate);GlobalUnlock(original);}
                    }
                }
                if(duplicate==IntPtr.Zero)throw new InvalidOperationException("Clipboard duplication failed");
                result.Add(new(format,duplicate,kind));
            }
            return result;
        }catch {FreeFormats(result);throw;}
        finally {CloseClipboard();}
    }
    private static void FreeFormats(List<ClipboardFormat> formats) {
        foreach(var format in formats){
            if(format.Data==IntPtr.Zero)continue;
            if(format.Kind==ClipboardKind.Bitmap)DeleteObject(format.Data);else GlobalFree(format.Data);
        }
    }
    internal static ClipboardKind ClassifyClipboardFormat(uint format,string name) {
        if(format is 2 or 0x82)return ClipboardKind.Bitmap;
        if(format is 1 or 4 or 5 or 6 or 7 or 8 or 10 or 11 or 12 or 13 or 15 or 16 or 17)return ClipboardKind.Memory;
        if(format<0xc000)return ClipboardKind.Unsupported;
        if(name is "DataObject" or "Ole Private Data")return ClipboardKind.Internal;
        return name is "PNG" or "image/png" or "HTML Format" or "Rich Text Format" or "Rich Text Format Without Objects" or
            "text/html" or "text/plain" or "text/plain;charset=utf-8" or "text/plain;charset=utf-16" or
            "UniformResourceLocator" or "UniformResourceLocatorW" or "Preferred DropEffect" or "FileName" or "FileNameW" or
            "Shell IDList Array" or "CanIncludeInClipboardHistory" or "CanUploadToCloudClipboard"
            ?ClipboardKind.Memory:ClipboardKind.Unsupported;
    }
    public void Dispose(){commands.CompleteAdding();thread.Join(4000);}
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap {internal int Type,Width,Height,WidthBytes;internal ushort Planes,BitsPixel;internal IntPtr Bits;}
    [DllImport("gdi32.dll",EntryPoint="GetObjectW")] private static extern int GetObject(IntPtr handle,int size,out NativeBitmap bitmap);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr CopyImage(IntPtr handle,uint type,int x,int y,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClipboardFormatName(uint format,StringBuilder name,int capacity);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags,nuint bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll",EntryPoint="RtlMoveMemory")] private static extern void CopyMemory(IntPtr target,IntPtr source,nuint bytes);
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
