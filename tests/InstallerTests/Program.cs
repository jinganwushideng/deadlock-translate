using DeadlockTranslate.Setup;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));var temp=Path.Combine(root,"artifacts","gui-package-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);int passed=0;
void Check(bool v,string n){if(!v)throw new Exception(n);passed++;Console.WriteLine("PASS "+n);}
var history=ReleaseHistory.Load();
Check(history.Current.Version==InstallerVersion.Current,"embedded release notes select current installer version");
var announcements=new InstallerAnnouncement(Path.Combine(temp,"announcement-marker"));
Check(announcements.ShouldShow("0.6.24"),"first launch of a version needs an announcement");announcements.MarkSeen("0.6.24");
Check(!new InstallerAnnouncement(Path.Combine(temp,"announcement-marker")).ShouldShow("0.6.24"),"moving or reopening an installer version cannot repeat its announcement");
Check(announcements.ShouldShow("0.6.25"),"a later version has its own first-open announcement");announcements.MarkSeen("0.6.25");
Check(!announcements.ShouldShow("0.6.24")&&!announcements.ShouldShow("0.6.25"),"announcement history survives alternating installer versions");
var firstRevision=InstallerAnnouncement.ContentRevision(history.Current);
Check(announcements.ShouldShow("0.6.24",firstRevision),"legacy version-only marker does not hide revised same-version notes");
announcements.MarkSeen("0.6.24",firstRevision);
Check(!new InstallerAnnouncement(Path.Combine(temp,"announcement-marker")).ShouldShow("0.6.24",firstRevision),"same content remains seen when installer is moved or reopened");
Check(firstRevision==InstallerAnnouncement.ContentRevision(history.Current with{Changes=history.Current.Changes.ToArray()}),"announcement identity depends on content rather than array identity");
var secondRevision=InstallerAnnouncement.ContentRevision(history.Current with{Title=history.Current.Title+" 修订"});
Check(announcements.ShouldShow("0.6.24",secondRevision),"same version with changed announcement displays once again");
announcements.MarkSeen("0.6.24",secondRevision);
Check(!announcements.ShouldShow("0.6.24",firstRevision)&&!announcements.ShouldShow("0.6.24",secondRevision),"alternating same-version revisions keep their read state");
Check(!announcements.ShouldShow("0.6.24"),"revision-aware marker remains compatible with version-only checks");
Check(history.Current.Version=="0.7.0"&&history.Current.Title.Contains("设置界面")&&history.Current.Changes[0].Contains("设置界面")&&history.Current.Changes.Any(x=>x.Contains("发送")),"current announcement leads with the final settings and sending changes");
Check(history.Entries.Any(e=>e.Version=="0.6.24"&&e.Changes.Any(x=>x.Contains("备份"))),"previous backup-cleanup update remains in history");
Check(typeof(ReleaseNotesWindow).Assembly.GetManifestResourceNames().Contains("community-qr.png"),"feedback QR is embedded for offline display");
using(var maintenance=typeof(ReleaseNotesWindow).Assembly.GetManifestResourceStream("scripts/Backup-Maintenance.ps1")){
 Check(maintenance!=null,"backup maintenance helper is embedded in the offline installer");
 using var reader=new StreamReader(maintenance!);Check(reader.ReadToEnd().Contains("function Invoke-DltBackupCleanup"),"embedded helper contains the shared cleanup policy");
}
Check(Enumerable.Range(0,21).All(i=>history.Entries.Any(e=>e.Version=="0.6."+i)),"all existing 0.6.x releases remain in installer history");
Check(history.Entries.Any(e=>e.Version=="0.5.0")&&history.Entries.Any(e=>e.Version=="0.4.0"),"earlier release and source-only records remain available");
Check(history.Entries.Select(e=>e.Version).Distinct().Count()==history.Entries.Count,"repeated same-version changelog sections appear as one selectable version");
foreach(var name in new[]{"../outside","/absolute","C:/drive","a/b:stream","a/CON.txt","a/trailing.","a/space ","a//b","a/../b"}){bool failed=false;try{Package.SafePath(temp,name);}catch(InvalidDataException){failed=true;}Check(failed,"unsafe path rejected "+name);}
Check(Package.SafePath(temp,"中文 空格/file.txt").StartsWith(temp),"Unicode paths remain inside chosen root");
var audit=new InstallerAudit(temp);var secret="sk-"+new string('X',32);audit.Record("failure",new{message="Authorization: Bearer "+secret});
using(var json=JsonDocument.Parse(audit.Export())){Check(!audit.Export().Contains(secret),"diagnostic export redacts credential formats");Check(json.RootElement.GetProperty("events")[0].GetProperty("data").TryGetProperty("architecture",out _),"diagnostics include architecture and version");}
using(var identity=System.Security.Principal.WindowsIdentity.GetCurrent()){
 var value=new ResumeState(identity.User!.Value,"fixture",DateTimeOffset.UtcNow,"install",@"D:\游戏",@"D:\Apps\中文",@"D:\Steam","123",false,true,true,true,false,false);
 var file=value.Save();var restored=ResumeState.Load(file,value.Owner,value.Version);Check(restored==value&&!File.Exists(file),"administrator resume retains whitelisted choices and consumes owned file");
 foreach(var bad in new[]{value with{Owner="S-1-5-invalid"},value with{Version="old"},value with{Created=DateTimeOffset.UtcNow.AddHours(-1)},value with{Mode="execute"}}){file=bad.Save();bool rejected=false;try{ResumeState.Load(file,value.Owner,value.Version);}catch(IOException){rejected=true;}finally{File.Delete(file);}Check(rejected,"invalid resume owner version age or mode rejected");}
 bool outside=false;try{ResumeState.Load(Path.Combine(temp,"resume-"+Guid.NewGuid().ToString("N")+".json"),value.Owner,value.Version);}catch(IOException){outside=true;}Check(outside,"resume file outside installer state rejected");
}
string Make(string id,bool badHash=false,string? extra=null,bool duplicate=false){
 var archive=Path.Combine(temp,id+".zip");using var zip=ZipFile.Open(archive,ZipArchiveMode.Create);var files=new List<object>();
 foreach(var name in new[]{"DeadlockTranslate.exe","dlt_translate_dir.vpk","pairing.key","wwwroot/index.html","data/terms.json"}){var bytes=System.Text.Encoding.UTF8.GetBytes("fixture "+name);using(var s=zip.CreateEntry("package/"+name).Open())s.Write(bytes);files.Add(new{file=name,sha256=badHash?new string('0',64):Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()});}
 using(var writer=new StreamWriter(zip.CreateEntry("package/package-manifest.json").Open()))writer.Write(JsonSerializer.Serialize(new{version="0.6.8",files}));
 if(extra!=null)using(zip.CreateEntry(extra).Open()){}
 if(duplicate)using(zip.CreateEntry("package/PAIRING.KEY").Open()){}
 return archive;
}
var good=Package.Extract(Make("valid"),Path.Combine(temp,"valid"),CancellationToken.None);Check(File.Exists(Path.Combine(good,"wwwroot/index.html")),"valid archive extracted and every file hash verified");
foreach(var c in new[]{("bad-hash",true,(string?)null,false),("zip-slip",false,"../outside.txt",false),("case-alias",false,null,true),("unlisted",false,"package/extra.txt",false)}){
 bool failed=false;try{Package.Extract(Make(c.Item1,c.Item2,c.Item3,c.Item4),Path.Combine(temp,c.Item1),CancellationToken.None);}catch(InvalidDataException){failed=true;}Check(failed,"bad archive rejected "+c.Item1);
}
using(var cancel=new CancellationTokenSource()){cancel.Cancel();bool cancelled=false;try{Package.Extract(Make("cancel"),Path.Combine(temp,"cancel"),cancel.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"cancelled preparation cannot continue extracting");}
File.WriteAllText(Path.Combine(root,"artifacts","gui-package-tests.json"),JsonSerializer.Serialize(new{passed,realInstallation=false,certificateTrustChanged=false}));
Exception? uiError=null;
var uiThread=new Thread(()=>{
 try{
  using var wizard=new Wizard(preview:true);var preview=Path.Combine(temp,"installer-ui.png");wizard.SavePreview(preview);
  using var layout=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(preview,"layout.json")));
  Check(layout.RootElement.GetProperty("row").GetProperty("Height").GetInt32()>0,"input rows retain height after responsive layout");
  Check(layout.RootElement.GetProperty("mode").GetProperty("Height").GetInt32()<=layout.RootElement.GetProperty("row").GetProperty("Height").GetInt32(),"dropdown fits its input row");
  Check(layout.RootElement.GetProperty("updateHistoryAvailable").GetBoolean(),"release notes entry is visible without a settings or browser dependency");
  Check(new[]{"busy","error","locations","review","readonly","recovery","reinstall","complete","small"}.All(n=>File.Exists(Path.Combine(temp,"installer-ui-"+n+".png"))),"disabled, error, readonly, VPK recovery, orphan reinstall and small-window preview states render");
  using var notes=new ReleaseNotesWindow();
  Check(notes.SelectedVersion==InstallerVersion.Current&&notes.RenderedText.Contains(history.Current.Title)&&history.Current.Changes.All(notes.RenderedText.Contains),"release notes default to current version and all current feature text");
  notes.SelectVersion("0.6.0");Check(notes.RenderedText.Contains("多语言收发")&&!notes.RenderedText.Contains("安装器新增离线更新日志"),"selecting old version replaces the content rather than appending current notes");
  notes.SelectVersion("0.4.0");Check(notes.RenderedText.Contains("历史源码版本记录"),"source-only history is distinguished from an installer release");
  var notesFolder=Path.Combine(temp,"release-notes");ReleaseNotesWindow.SavePreviews(notesFolder);
  Check(new[]{"current","history","small"}.All(n=>File.Exists(Path.Combine(notesFolder,n+".png"))),"current history and compact release notes layouts render offline");
 }catch(Exception e){uiError=e;}
});uiThread.SetApartmentState(ApartmentState.STA);uiThread.Start();uiThread.Join();if(uiError!=null)throw uiError;
File.WriteAllText(Path.Combine(root,"artifacts","gui-package-tests.json"),JsonSerializer.Serialize(new{passed,realInstallation=false,certificateTrustChanged=false}));
