using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DeadlockTranslate;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
if(args.FirstOrDefault()=="--clipboard-child"){await ClipboardNativeFixture.Run(args[1],args[2]);return;}

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
Check(plan.Fields?.Any(field=>field.Text=="Unseen item annotation"&&field.ItemId==123&&field.Group==0&&field.Entry==0)==true,"unseen item annotations keep native item ID and field position");
Check(plan.Texts.SequenceEqual(new[]{"Test build","Build introduction","Early group","Group note","Unseen item annotation","Ability note"}),"complete protobuf contains unrendered annotations and ability notes");
Check(BuildSourceCapture.MatchesExportTitle("[Haze build [55% WR]]的副本","Haze build [55% WR]"),"known native copy titles validate the original complete title");
Check(!BuildSourceCapture.MatchesExportTitle("[Vindicta build]的副本","Haze build"),"a native ID cannot authorize renaming another hero's exported source");
Check(!BuildSourceCapture.MatchesExportTitle("[Haze build [61% WR]]的副本","Haze build [55% WR]"),"source validation retains title numbers");
Check(BuildSourceCapture.IsConsistent(plan)&&!BuildSourceCapture.IsConsistent(plan with{Name="Wrong title"})&&!BuildSourceCapture.IsConsistent(plan with{Hero=3}),"stored sources retain their original native title and hero identity");
Check(BuildSourceCapture.ClassifyClipboardFormat(2,"")==BuildSourceCapture.ClipboardKind.Bitmap&&BuildSourceCapture.ClassifyClipboardFormat(13,"")==BuildSourceCapture.ClipboardKind.Memory,"bitmap and text handles use distinct native clipboard operations");
Check(BuildSourceCapture.ClassifyClipboardFormat(0xc009,"DataObject")==BuildSourceCapture.ClipboardKind.Internal&&BuildSourceCapture.ClassifyClipboardFormat(0xc010,"Unknown OLE object")==BuildSourceCapture.ClipboardKind.Unsupported,"opaque OLE clipboard formats never reach GlobalSize or byte copying");
var sourceOrder=BuildTextJobs.PrioritizeSource(plan with{Texts=["Unseen item annotation","Group note","Test build","Ability note","Build introduction","Early group"]},["Group note"]);
Check(sourceOrder[0]=="Test build"&&sourceOrder[1]=="Early group"&&Array.IndexOf(sourceOrder,"Group note")<Array.IndexOf(sourceOrder,"Unseen item annotation"),"native title, group names and visible instructions precede equipment annotations");
Check(BuildSourceCapture.SourceFingerprint(plan)==BuildSourceCapture.SourceFingerprint(plan with{Texts=plan.Texts.Reverse().ToArray(),Fields=plan.Fields?.Reverse().ToArray()}),"local cache and export text enumeration produce the same complete source fingerprint");
Check(BuildSourceCapture.SourceFingerprint(plan)!=BuildSourceCapture.SourceFingerprint(plan with{Texts=plan.Texts.Append("Changed hidden annotation").ToArray()}),"changed complete contents invalidate a snapshot even with the same native ID and version");
Check(LocalBuildCatalog.Select([plan],"Test build [61% winrate]",null,[]).Build?.Id==101,"changing win-rate badges match the original build");
var other=plan with{Id=102,Groups=[new("Early group","Different note"),new("New section","New note")]};
Check(LocalBuildCatalog.Select([plan,other],"Test build",null,[]).Status=="ambiguous","same-name different builds are not guessed");
Check(LocalBuildCatalog.Select([plan,other],"Test build",null,[new("Early group","")]).Build==null,"shared words cannot select a scheme");
Check(LocalBuildCatalog.Select([plan,other],"Test build",null,[new("Early group","Different note")]).Build==null,"partial exact matches cannot disambiguate a same-name build");
Check(LocalBuildCatalog.Select([plan,other],"Test build",null,[new("New section","New note"),new("Early group","Different note")]).Build?.Id==102,"exact complete group structure disambiguates without relying on panel order");
Check(LocalBuildCatalog.Select([plan,other],"Test build",102,[]).Build?.Id==102,"native build ID takes priority");
Check(LocalBuildCatalog.Select([plan,other],"Other name",102,[]).Status=="mismatch","stale native ID is rejected during a scene swap");
Check(LocalBuildCatalog.Select([plan],"Test build",null,[new("Late [56k]","Group note")]).Status=="mismatch","unique title alone does not override contradictory visible content");
var twoGroups=plan with{Groups=[new("Core Build","Buy in order"),new("Early","Buy as needed")]};
var shopGroups=new BuildVisibleGroup[]{new("Core Build",""),new("Early","Buy as needed"),new("Counters/Upgrades","")};
Check(LocalBuildCatalog.Select([twoGroups],"Test build",null,shopGroups).Build?.Id==101,"all stored groups identify a uniquely titled plan with collapsed notes and extra shop groups");
Check(LocalBuildCatalog.Select([twoGroups,other],"Test build",null,shopGroups).Build==null,"collapsed group tolerance cannot pick between same-name plans");
Check(LocalBuildCatalog.Select([twoGroups],"Test build",null,[new("Core Build",""),new("Early","Wrong instruction"),new("Counters/Upgrades","")]).Build==null,"different visible instruction still rejects the local plan");
Check(LocalBuildCatalog.Select([plan],"Test build [50% armor]",null,[]).Status=="not-found","actual title content is kept when comparing builds");
Check(LocalBuildCatalog.Select([plan,plan with{Version=2}],"Test build",null,[]).Build?.Version==2,"duplicate saved entries select the latest version");
Check(LocalBuildCatalog.Select([plan],"Unknown",null,[]).Status=="not-found","unmatched builds cannot translate another scheme");
try{LocalBuildCatalog.Parse([10,127]);throw new Exception("Accepted truncated data");}catch(InvalidDataException){Check(true,"truncated protobuf is rejected");}
try{LocalBuildCatalog.Parse(Data(1,Data(5,[0xFF])));throw new Exception("Accepted invalid UTF8");}catch(DecoderFallbackException){Check(true,"invalid UTF8 is rejected");}

var folder=Path.Combine(Path.GetTempPath(),"dlt-catalog-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
try{
    var reviewFolder=Path.Combine(folder,"review");var snapshots=Path.Combine(reviewFolder,"build-sources-v2");Directory.CreateDirectory(snapshots);
    var invalidKey=new string('F',64);var invalidFile=Path.Combine(snapshots,invalidKey+".json");
    File.WriteAllBytes(invalidFile,System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new{Account=LocalBuildCatalog.LocateCurrentCache()??"",Plan=plan with{Name="Wrong title",Fingerprint=invalidKey}}));
    using(var reviewed=new BuildSourceCapture(reviewFolder)){
        Check(reviewed.Find(invalidKey)==null,"mismatched legacy snapshots are refused without modifying their files");
        Check(reviewed.NeedsCacheRefresh(plan)&&!reviewed.NeedsCacheRefresh(plan with{Id=999}),"only proven mismatched source scopes receive a fresh quality cache namespace");
    }
    File.Delete(invalidFile);
    using(var reviewed=new BuildSourceCapture(reviewFolder))Check(reviewed.NeedsCacheRefresh(plan),"targeted cache repair survives old snapshot cleanup and restart");
    var translationCache=new BuildTextCache(folder);
    var longTranslation=new string('译',8000);
    translationCache.Store(new string('A',64),longTranslation);translationCache.Flush();
    Check(new BuildTextCache(folder).TryGet(new string('A',64),out var savedLong)&&savedLong==longTranslation,"expanded long annotations survive cache flush and restart");
    var job=new BuildTextJobs.Job("fixture",default,2);
    var longSources=new[]{new string('a',6000),new string('b',6000),new string('c',6000)};
    job.SetPlan(new("fixture",longSources,null,new string('B',64),false));
    job.Apply(longSources,System.Text.Json.JsonSerializer.SerializeToElement(new{translations=new[]{longTranslation,longTranslation,longTranslation},translated=3,cacheHits=0}),false);
    var page=System.Text.Json.JsonSerializer.SerializeToElement(job.Snapshot(0,1),ConfigStore.Json);
    Check(page.GetProperty("fields").GetArrayLength()==1&&page.GetProperty("more").GetBoolean(),"long results paginate before overflowing native HTML transport");
    var fullJob=new BuildTextJobs.Job("fixture",default,2);
    fullJob.SetPlan(new("fixture",longSources,[new("heading",longTranslation)],new string('B',64),false,null,true));
    var smallMeta=System.Text.Json.JsonSerializer.SerializeToElement(fullJob.Snapshot(0,1),ConfigStore.Json);
    Check(smallMeta.GetProperty("plan").GetProperty("groups").ValueKind==System.Text.Json.JsonValueKind.Null,"complete source results do not resend group descriptions as page metadata");
    var telemetry=new LlmTelemetry();
    string ResponseJson(string content)=>System.Text.Json.JsonSerializer.Serialize(new{choices=new[]{new{message=new{content},finish_reason="stop"}}});
    using(var response=new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent(ResponseJson(longTranslation))}){
        try{await ChatCompletionReader.Read(response,telemetry.Begin(new Settings(),false,0),default);throw new Exception("Chat bound was removed");}
        catch(InvalidDataException){Check(true,"ordinary chat retains its small response bound");}
    }
    using(var response=new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent(ResponseJson(longTranslation))}){
        Check(await ChatCompletionReader.Read(response,telemetry.Begin(new Settings(),false,0),default,1000000)==longTranslation,"build response bound accepts complete long batch content");
    }
    var delta=System.Text.Json.JsonSerializer.Serialize(new{choices=new[]{new{index=0,delta=new{content=longTranslation},finish_reason="stop"}}});
    using(var response=new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent("data: "+delta+"\n\ndata: [DONE]\n\n",Encoding.UTF8,"text/event-stream")}){
        Check(await ChatCompletionReader.Read(response,telemetry.Begin(new Settings(),false,0),default,1000000)==longTranslation,"build streaming response uses the same explicit bound");
    }
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

