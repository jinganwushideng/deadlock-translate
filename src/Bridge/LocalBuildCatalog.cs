using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ValveKeyValue;
using ValveResourceFormat.ResourceTypes;

namespace DeadlockTranslate;

public sealed record BuildVisibleGroup(string Name,string Note);
internal sealed record BuildSourceField(string Id,string Kind,string Text,uint ItemId=0,int Group=-1,int Entry=-1);
internal sealed record LocalHeroBuild(uint Id,uint Hero,uint Version,uint Updated,string Name,string[] Texts,BuildVisibleGroup[] Groups,string Description,string Fingerprint,BuildSourceField[]? Fields=null,byte[]? Serialized=null);
internal sealed record LocalBuildSelection(string Status,LocalHeroBuild? Build);

// Reads a snapshot of the current Steam user's cache. No write handle is opened.
internal sealed class LocalBuildCatalog {
    private const int MaxFileBytes=8*1024*1024;
    private readonly Func<string?> locate;
    private string? loadedFile;
    private long loadedLength;
    private DateTime loadedTime;
    private LocalHeroBuild[] builds=[];

    internal LocalBuildCatalog(Func<string?>? locate=null) { this.locate=locate??LocateCurrentCache; }

    internal async Task<LocalBuildSelection> Find(string title,uint? id,BuildVisibleGroup[] groups,CancellationToken ct,string description="") {
        try{
            var file=locate();
            if(file==null)return new("account-unavailable",null);
            var info=new FileInfo(file);
            if(!info.Exists)return new("cache-missing",null);
            if(info.Length>MaxFileBytes)return new("cache-too-large",null);
            if(file!=loadedFile||info.Length!=loadedLength||info.LastWriteTimeUtc!=loadedTime){
                var length=info.Length;
                var modified=info.LastWriteTimeUtc;
                var parsed=await Task.Run(()=>Read(file),ct);
                ct.ThrowIfCancellationRequested();
                info.Refresh();
                if(!info.Exists||info.Length!=length||info.LastWriteTimeUtc!=modified)return new("cache-changing",null);
                builds=parsed;loadedFile=file;loadedLength=length;loadedTime=modified;
            }
            return Select(builds,title,id,groups,description);
        }catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException or KeyNotFoundException or InvalidOperationException or NotSupportedException){
            return new("cache-unreadable",null);
        }
    }

    internal static LocalBuildSelection Select(LocalHeroBuild[] builds,string title,uint? id,BuildVisibleGroup[] groups,string description="") {
        var candidates=builds.Where(build=>id is >0?build.Id==id:TitlesMatch(build.Name,title)).ToArray();
        if(candidates.Length==0)return new("not-found",null);
        // Keep separate revisions until the displayed structure has confirmed which one is active.
        if(!string.IsNullOrWhiteSpace(title))candidates=candidates.Where(build=>TitlesMatch(build.Name,title)).ToArray();
        if(candidates.Length==0)return new("mismatch",null);
        var ambiguousName=candidates.Select(build=>(build.Id,build.Hero,Anonymous:build.Id==0?build.Fingerprint:"")).Distinct().Skip(1).Any();
        candidates=candidates.Where(build=>(GroupMatch(build.Groups,groups,ambiguousName&&id is not >0)||
            !ambiguousName&&CoversStoredGroups(build.Groups,groups))&&
            (description.Length==0||Normalize(build.Description)==Normalize(description))).ToArray();
        if(candidates.Length==0)return new(ambiguousName?"ambiguous":"mismatch",null);
        candidates=candidates.GroupBy(build=>(build.Id,build.Hero,Anonymous:build.Id==0?build.Fingerprint:""))
            .Select(group=>group.OrderByDescending(build=>build.Version).ThenByDescending(build=>build.Updated).First()).ToArray();
        if(candidates.Length>1)return new("ambiguous",null);
        return new("ready",candidates[0]);
    }

    private static bool GroupMatch(BuildVisibleGroup[] stored,BuildVisibleGroup[] visible,bool requireComplete) {
        if(requireComplete&&(visible.Length==0||visible.Length!=stored.Length))return false;
        var remaining=stored.Select(NormalizeGroup).ToList();
        foreach(var group in visible.Select(NormalizeGroup)){
            var index=remaining.IndexOf(group);
            if(index<0)return false;
            remaining.RemoveAt(index);
        }
        return true;
    }
    private static BuildVisibleGroup NormalizeGroup(BuildVisibleGroup group)=>new(Normalize(group.Name),Normalize(group.Note));
    private static bool CoversStoredGroups(BuildVisibleGroup[] stored,BuildVisibleGroup[] visible) {
        // Collapsed headers can omit their note, and the shop can add extra groups.
        // Require a unique full-title candidate and every named stored group.
        if(stored.Length<2||visible.Length<stored.Length)return false;
        var actual=visible.Select(NormalizeGroup).ToList();
        foreach(var group in stored.Select(NormalizeGroup)){
            if(group.Name.Length==0)return false;
            var index=actual.FindIndex(item=>item.Name==group.Name&&(item.Note.Length==0||item.Note==group.Note));
            if(index<0)return false;
            actual.RemoveAt(index);
        }
        return true;
    }
    private static string Normalize(string value)=>Regex.Replace(value,@"\s+"," ",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)).Trim();
    private static bool TitlesMatch(string a,string b)=>Canonical(a)==Canonical(b);
    internal static string Canonical(string value) {
        value=Regex.Replace(value,@"</?(?:b|i|u|font|span|strong|em|br)(?:\s[^<>]*)?\s*/?>"," ",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        value=System.Net.WebUtility.HtmlDecode(value);
        value=Regex.Replace(value,@"\[\s*\d+(?:[.,]\d+)?\s*%\s*(?:WR|win\s*rate|胜率)\s*\]"," ",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        return Regex.Replace(Normalize(value),@"\s*\[","[",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)).ToUpperInvariant();
    }

    internal static string? LocateCurrentCache() {
        using var steam=Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var root=steam?.GetValue("SteamPath") as string;
        if(string.IsNullOrWhiteSpace(root)||!Directory.Exists(root))return null;
        uint user=0;
        using(var process=Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess")){
            if(process?.GetValue("ActiveUser") is int active)user=unchecked((uint)active);
        }
        if(user==0){
            var logins=Path.Combine(root,"config","loginusers.vdf");
            if(!File.Exists(logins)||new FileInfo(logins).Length>1024*1024)return null;
            using var input=File.OpenRead(logins);
            var document=KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(input);
            var recent=document.Root.Children.Where(pair=>pair.Value.TryGetValue("MostRecent",out var flag)&&flag.ToString()=="1").ToArray();
            var autoLogin=steam?.GetValue("AutoLoginUser") as string;
            if(!string.IsNullOrWhiteSpace(autoLogin))
                recent=document.Root.Children.Where(pair=>pair.Value.TryGetValue("AccountName",out var name)&&
                    name.ToString().Equals(autoLogin,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(recent.Length!=1||!ulong.TryParse(recent[0].Key,out var steamId)||steamId<=76561197960265728||
                steamId-76561197960265728>uint.MaxValue)return null;
            user=(uint)(steamId-76561197960265728);
        }
        return Path.Combine(root,"userdata",user.ToString(System.Globalization.CultureInfo.InvariantCulture),"1422450","remote","cfg","cached_hero_builds.kv3");
    }

    internal static LocalHeroBuild[] Read(string path) {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(file.Length is <64 or >MaxFileBytes)throw new InvalidDataException("Build cache size");
        var bytes=new byte[(int)file.Length];file.ReadExactly(bytes);
        ValidateHeader(bytes);
        using var stream=new MemoryStream(bytes,false);
        using var reader=new BinaryReader(stream,Encoding.UTF8);
        var binary=new BinaryKV3{Resource=null!,Size=(uint)bytes.Length};
        binary.Read(reader);
        var document=binary.Data;
        var result=new List<LocalHeroBuild>();
        foreach(var name in new[]{"Favorites","Unpublished","SavedLastUsed"}){
            if(!document.Root.TryGetValue(name,out var section)||(!section.IsCollection&&!section.IsArray))continue;
            if(section.Count>2048)throw new InvalidDataException("Build cache entries");
            foreach(var entry in section.IsArray?section.AsArraySpan().ToArray():section.Values){
                if(entry.ValueType!=KVValueType.BinaryBlob)continue;
                try{
                    var build=Parse(entry.AsBlob());
                    if(build!=null)result.Add(build);
                }catch(Exception ex) when(ex is InvalidDataException or DecoderFallbackException or OverflowException){}
            }
        }
        return result.ToArray();
    }

    private static void ValidateHeader(byte[] data) {
        var magic=BinaryPrimitives.ReadUInt32LittleEndian(data);
        var version=(int)(magic&0xFF);
        if((magic&0xFFFFFF00)!=0x4B563300||version is <1 or >5)throw new InvalidDataException("Build cache version");
        int At(int offset)=>BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset,4));
        const int maximum=32*1024*1024;
        var size=At(version==1?36:48);
        var blobs=version==1?0:At(60);
        if(size<0||blobs<0||(long)size+blobs>maximum)throw new InvalidDataException("Build cache decoded size");
        if(version>=5){
            if(data.Length<120||At(72)<0||At(80)<0||(long)At(72)+At(80)!=size)
                throw new InvalidDataException("Build cache buffer sizes");
        }
    }

    internal static LocalHeroBuild? Parse(byte[] bytes) {
        if(bytes.Length>65536)throw new InvalidDataException("Build blob size");
        var outer=new BuildWireReader(bytes);
        while(outer.Next(out var field,out var wire,out var number,out var data)){
            if(field==1&&wire==2)return ParseBuild(data);
        }
        return null;
    }
    internal static LocalHeroBuild ParseExport(byte[] compressed) {
        if(compressed.Length>1024*1024)throw new InvalidDataException("Build export too large");
        using var decoder=new ZstdSharp.Decompressor();
        var bytes=decoder.Unwrap(compressed,4*1024*1024).ToArray();
        return ParseBuild(bytes)??throw new InvalidDataException("Invalid build export");
    }

    private static LocalHeroBuild? ParseBuild(ReadOnlySpan<byte> bytes) {
        uint id=0,hero=0,version=0,updated=0;
        var title="";
        var description="";
        var texts=new List<string>();
        var groups=new List<BuildVisibleGroup>();
        var fields=new List<BuildSourceField>();
        var reader=new BuildWireReader(bytes);
        while(reader.Next(out var field,out var wire,out var number,out var data)){
            if(wire==0){
                switch(field){
                    case 1:id=checked((uint)number);break;
                    case 2:hero=checked((uint)number);break;
                    case 4:updated=checked((uint)number);break;
                    case 8:version=checked((uint)number);break;
                }
            }else if(wire==2){
                switch(field){
                    case 5:title=ReadText(data);AddText(texts,title);fields.Add(new("title","title",title));break;
                    case 6:description=ReadText(data);AddText(texts,description);fields.Add(new("description","description",description));break;
                    case 10:ReadDetails(data,texts,groups,fields);break;
                }
            }
        }
        var unique=texts.Distinct(StringComparer.Ordinal).ToArray();
        if(string.IsNullOrWhiteSpace(title)||unique.Length>256||unique.Sum(text=>text.Length)>64000)return null;
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{id,hero,version,title,description,groups,unique})));
        return new(id,hero,version,updated,title,unique,groups.ToArray(),description,fingerprint,fields.ToArray(),bytes.ToArray());
    }

    private static readonly UTF8Encoding Utf8=new(false,true);
    private static string ReadText(ReadOnlySpan<byte> bytes) {
        if(bytes.Length>24000)throw new InvalidDataException("Build text size");
        var text=Utf8.GetString(bytes).Trim();
        if(text.Length>6000)throw new InvalidDataException("Build text length");
        return text;
    }
    private static void AddText(List<string> texts,string text) {
        if(text.Length!=0&&!text.StartsWith('#')&&!text.StartsWith('{')&&text.Any(char.IsLetter))texts.Add(text);
        if(texts.Count>2048)throw new InvalidDataException("Build text count");
    }
    private static void ReadDetails(ReadOnlySpan<byte> bytes,List<string> texts,List<BuildVisibleGroup> groups,List<BuildSourceField> fields) {
        var details=new BuildWireReader(bytes);
        while(details.Next(out var field,out var wire,out _,out var group)){
            if(wire!=2)continue;
            var name="";
            var note="";
            var index=groups.Count;
            var entry=0;
            var reader=new BuildWireReader(group);
            while(reader.Next(out var child,out var childWire,out _,out var value)){
                if(childWire!=2)continue;
                if(field==1&&child is 2 or 3){
                    var text=ReadText(value);
                    if(child==2)name=text;else note=text;
                    AddText(texts,text);
                    fields.Add(new("groups/"+index+"/"+(child==2?"name":"note"),child==2?"group-name":"group-note",text,Group:index));
                }
                else if(child==1&&(field==1||field==2)){
                    var item=new BuildWireReader(value);
                    uint itemId=0;
                    var annotationText="";
                    while(item.Next(out var itemField,out var itemWire,out var number,out var annotation)){
                        if(itemField==1&&itemWire==0)itemId=checked((uint)number);
                        if(itemWire==2&&itemField==(field==1?2:4))annotationText=ReadText(annotation);
                    }
                    AddText(texts,annotationText);
                    fields.Add(new((field==1?"groups/"+index+"/items/":"abilities/")+entry+"/note",
                        field==1?"item-note":"ability-note",annotationText,itemId,field==1?index:-1,entry));
                    entry++;
                }
            }
            if(field==1&&(name.Length!=0||note.Length!=0))groups.Add(new(name,note));
        }
    }
}

internal ref struct BuildWireReader(ReadOnlySpan<byte> source) {
    private ReadOnlySpan<byte> remaining=source;
    private int fields;
    internal bool Next(out int field,out int wire,out ulong value,out ReadOnlySpan<byte> bytes) {
        field=0;wire=0;value=0;bytes=default;
        if(remaining.IsEmpty)return false;
        if(++fields>8192)throw new InvalidDataException("Build protobuf field count");
        var tag=Varint();field=checked((int)(tag>>3));wire=(int)(tag&7);
        if(field==0)throw new InvalidDataException("Build protobuf field");
        if(wire==0){value=Varint();return true;}
        var length=wire switch{1=>8,5=>4,2=>checked((int)Varint()),_=>throw new InvalidDataException("Build protobuf wire type")};
        if(length<0||length>remaining.Length)throw new InvalidDataException("Build protobuf truncated");
        bytes=remaining[..length];remaining=remaining[length..];return true;
    }
    private ulong Varint() {
        ulong result=0;
        for(var shift=0;shift<70;shift+=7){
            if(remaining.IsEmpty)throw new InvalidDataException("Build protobuf varint");
            var current=remaining[0];remaining=remaining[1..];
            if(shift==63&&current>1)throw new InvalidDataException("Build protobuf integer");
            result|=(ulong)(current&127)<<shift;
            if(current<128)return result;
        }
        throw new InvalidDataException("Build protobuf integer");
    }
}
