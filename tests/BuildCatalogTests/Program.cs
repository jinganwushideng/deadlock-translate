using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DeadlockTranslate;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

var passed=0;
void Check(bool valid,string name){if(!valid)throw new Exception(name);Console.WriteLine("PASS "+name);passed++;}
byte[] Join(params byte[][] parts)=>parts.SelectMany(p=>p).ToArray();
byte[] Int(ulong n){var bytes=new List<byte>();while(n>=128){bytes.Add((byte)((n&127)|128));n>>=7;}bytes.Add((byte)n);return bytes.ToArray();}
byte[] Number(int field,uint n)=>Join(Int((ulong)(field<<3)),Int(n));
byte[] Data(int field,byte[] value)=>Join(Int((ulong)((field<<3)|2)),Int((ulong)value.Length),value);
byte[] Text(int field,string value)=>Data(field,Encoding.UTF8.GetBytes(value));
byte[] Build(uint id,string name,uint version=1)=>Data(1,Join(Number(1,id),Number(2,17),Number(8,version),Text(5,name),Text(6,"Build introduction"),
    Data(10,Join(Data(1,Join(Text(2,"Early group"),Text(3,"Group note"),Data(1,Join(Number(1,123),Text(2,"Unseen item annotation"))))),
        Data(2,Data(1,Text(4,"Ability note")))))));
var plan=LocalBuildCatalog.Parse(Build(101,"Test build"))!;
Check(plan.Texts.SequenceEqual(new[]{"Test build","Build introduction","Early group","Group note","Unseen item annotation","Ability note"}),"complete protobuf contains unrendered annotations and ability notes");
Check(LocalBuildCatalog.Select([plan],"Test build [61% winrate]",null,[]).Build?.Id==101,"changing win-rate badges match the original build");
var other=plan with{Id=102,Texts=["Different content"]};
Check(LocalBuildCatalog.Select([plan,other],"Test build",null,[]).Status=="ambiguous","same-name different builds are not guessed");
Check(LocalBuildCatalog.Select([plan,other],"Test build",null,["Early group"]).Build?.Id==101,"visible hints disambiguate same-name builds");
Check(LocalBuildCatalog.Select([plan,other],"Any name",102,[]).Build?.Id==102,"native build ID takes priority");
Check(LocalBuildCatalog.Select([plan,plan with{Version=2}],"Test build",null,[]).Build?.Version==2,"duplicate saved entries select the latest version");
Check(LocalBuildCatalog.Select([plan],"Unknown",null,[]).Status=="not-found","unmatched builds cannot translate another scheme");
try{LocalBuildCatalog.Parse([10,127]);throw new Exception("Accepted truncated data");}catch(InvalidDataException){Check(true,"truncated protobuf is rejected");}
try{LocalBuildCatalog.Parse(Data(1,Data(5,[0xFF])));throw new Exception("Accepted invalid UTF8");}catch(DecoderFallbackException){Check(true,"invalid UTF8 is rejected");}

var folder=Path.Combine(Path.GetTempPath(),"dlt-catalog-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
try{
    var path=Path.Combine(folder,"cache.kv3");
    void Write(uint version=1){
        var root=KVObject.Collection();var section=KVObject.Array();section.Add(KVObject.Blob(Build(101,"Test build",version)));root.Add("Favorites",section);
        using var stream=File.Create(path);
        new BinaryKV3(root,default,BlockType.DATA){Resource=null!,SerializationVersion=5,SerializationCompressionMethod=KV3BinaryCompressionMethod.Lz4}.Serialize(stream);
    }
    Write();
    var before=SHA256.HashData(File.ReadAllBytes(path));
    var catalog=new LocalBuildCatalog(()=>path);
    var selected=await catalog.Find("Test build",null,[],default);
    Check(selected.Build?.Texts.Contains("Unseen item annotation")==true,"real binary KV3 v5 LZ4 decodes complete fixture");
    Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))),"read-only cache snapshot leaves source hash unchanged");
    Write(3);File.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddSeconds(1));
    Check((await catalog.Find("Test build",null,[],default)).Build?.Version==3,"changed game cache is reloaded");
    var data=File.ReadAllBytes(path);BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(48),int.MaxValue);File.WriteAllBytes(path,data);
    Check((await new LocalBuildCatalog(()=>path).Find("Test build",null,[],default)).Status=="cache-unreadable","oversized decoded cache is rejected before allocation");
    Check((await new LocalBuildCatalog(()=>path+"missing").Find("Test build",null,[],default)).Status=="cache-missing","missing cache has a distinct result");
    Check((await new LocalBuildCatalog(()=>null).Find("Test build",null,[],default)).Status=="account-unavailable","missing active account does not scan other users");
    if(args.Length>0){
        var input=args[0];var hash=SHA256.HashData(File.ReadAllBytes(input));var timer=System.Diagnostics.Stopwatch.StartNew();
        var actual=LocalBuildCatalog.Read(input);timer.Stop();
        Check(actual.Length>0&&actual.Any(b=>b.Texts.Length>20),"local inspection copy contains complete schemes");
        Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(input))),"local inspection copy remains unchanged");
        Console.WriteLine($"Local snapshot: {actual.Length} entries, {actual.Sum(b=>b.Texts.Length)} fields, {timer.Elapsed.TotalMilliseconds:F1} ms; no text logged.");
        Check((await new LocalBuildCatalog().Find(actual[0].Name,actual[0].Id,[],default)).Build!=null,"current Steam account fallback locates the real cache read-only");
    }
}finally{Directory.Delete(folder,true);}
Console.WriteLine($"{passed} build catalog checks passed.");

