using DeadlockTranslate;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

int passed=0;void Check(bool v,string name){if(!v)throw new Exception("FAIL: "+name);passed++;Console.WriteLine("PASS: "+name);}
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));var temp=Path.Combine(root,"artifacts","diagnostics-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
Check(HotkeyChord.Parse("LAlt+Space").Canonical=="LAlt+Space","default migration keeps the left Alt chord");
foreach(var chord in new[]{"Ctrl+Enter","RAlt+Space","Ctrl+Shift+T","F8","LCtrl+RShift+9"})Check(HotkeyChord.TryParse(chord,out _),"custom chord accepted "+chord);
foreach(var chord in new[]{"Space","A","Enter","Win+T","Alt+F4","Ctrl+Ctrl+X","Ctrl+","Ctrl+Mouse1","Ctrl+Escape"})Check(!HotkeyChord.TryParse(chord,out _),"reserved or ambiguous chord rejected "+chord);
Check(HotkeyChord.Parse("shift+ctrl+t").Canonical=="Ctrl+Shift+T","canonical modifier ordering");
var left=HotkeyChord.Parse("LAlt+Space");Check(left.Matches(KeyModifiers.LeftAlt)&&!left.Matches(KeyModifiers.RightAlt),"left and right Alt are distinct");
Check(!left.Matches(KeyModifiers.LeftAlt|KeyModifiers.LeftControl),"extra Ctrl never triggers left Alt binding");
Check(!left.Matches(KeyModifiers.LeftAlt|KeyModifiers.LeftWindows),"Windows modifier never triggers binding");
var context=new HotkeyContext(true,true,true,true,false,false);var state=new HotkeyState();state.Process(left,0x12,0,0,true,false,context);
Check(state.Process(left,32,0,0,true,false,context).Action==HotkeyAction.Trigger,"modifier events drive capture without asynchronous callback polling");
Check(state.Process(left,32,0,0,true,false,context).Action==HotkeyAction.Suppress,"autorepeat is suppressed, no second translation");
Check(state.Process(HotkeyChord.Parse("Ctrl+Enter"),32,0,0,false,true,context with{GameForeground=false}).Action==HotkeyAction.Suppress,"release paired with old swallowed key across config and focus change");
Check(state.SwallowedKey==0,"release clears state");
foreach(var denied in new[]{(context with{Enabled=false},LogReason.Disabled),(context with{ChatOpen=false},LogReason.NoChat),(context with{Fresh=false},LogReason.StaleBridge),(context with{GameForeground=false},LogReason.ForegroundOther)}){
 var machine=new HotkeyState();machine.Seed(KeyModifiers.LeftAlt);var result=machine.Process(left,32,0,0,true,false,denied.Item1);Check(result.Action==HotkeyAction.Pass&&result.Reason==denied.Item2,"non-game/closed/disabled/stale never swallowed "+denied.Item2);
}
var injected=new HotkeyState();injected.Seed(KeyModifiers.LeftAlt);Check(injected.Process(left,32,0,0x10,true,false,context).Reason==LogReason.Injected,"software-injected main key is observable but not sent");
var test=new HotkeyState();test.Seed(KeyModifiers.LeftAlt);Check(test.Process(left,32,0,0,true,false,new(false,false,false,false,true,true)).Action==HotkeyAction.Test,"explicit browser test does not send chat");test.RecoverRelease(false);Check(test.SwallowedKey==0,"recover missed keyup after a hook rebuild");
var config=new ConfigStore(Path.Combine(temp,"config"));Check(config.Current.OutgoingHotkey==HotkeyChord.Default,"old config gets safe default");config.Save(config.Current with{OutgoingHotkey="Ctrl+Enter"});Check(new ConfigStore(config.DirectoryPath).Current.OutgoingHotkey=="Ctrl+Enter","custom binding persists");
using var cancelled=new CancellationTokenSource(TimeSpan.FromSeconds(15));
await using(var log=new DiagnosticLog(Path.Combine(temp,"logs-normal"),2048,3,4096)){
 var collect=new GameLogCollector(log);
 Check(collect.Accept("private-session",[new(1,"hotkey-captured","private-message-body","team"),new(2,"send-submitted","private-message-body","team")])==2,"game metadata acknowledged");
 collect.Accept("private-session",[new(1,"hotkey-captured","private-message-body","team")]);
 collect.Accept("private-session",[new(3,"hud-rebound",Count:2)]);
 log.Record("backend",LogKind.StartupFailed,new(Stage:"SECRET_MUST_NOT_EXPORT",FailureType:"SECRET_MUST_NOT_EXPORT",Binding:"SECRET_MUST_NOT_EXPORT"));
 await log.FlushAsync(cancelled.Token);
 var initial=Encoding.UTF8.GetString((await log.SnapshotAsync())[0]);Check(initial.Split("hotkey-captured").Length-1==1,"repeated bridge batch does not duplicate log event");Check(!initial.Contains("private-message-body")&&!initial.Contains("private-session")&&!initial.Contains("SECRET_MUST_NOT_EXPORT"),"raw IDs, session and unknown string fields never persist");
 Check(initial.Contains("hud-rebound"),"HUD rebind diagnostics persist only typed metadata");
 collect.Accept("private-session",[new(4,"hud-bound","private-HUD-body"),new(5,"hud-written","private-HUD-body"),new(6,"hud-expired","private-HUD-body"),new(7,"hud-host-repaired","private-HUD-body"),new(8,"hud-hidden-repaired","private-HUD-body")]);
 await log.FlushAsync(cancelled.Token);var hudEvents=string.Join("",(await log.SnapshotAsync()).Select(Encoding.UTF8.GetString));
 Check(new[]{"hud-bound","hud-written","hud-expired","hud-host-repaired","hud-hidden-repaired"}.All(hudEvents.Contains)&&!hudEvents.Contains("private-HUD-body"),"anonymous HUD lifecycle stages survive typed collection without message IDs");
 collect.Accept("private-session",[new(9,"bridge-rebuilt"),new(10,"bridge-bound"),new(11,"bridge-reconnect"),new(12,"adapter-loop-resumed"),new(13,"adapter-root-moved")]);
 await log.FlushAsync(cancelled.Token);var recovery=string.Join("",(await log.SnapshotAsync()).Select(Encoding.UTF8.GetString));
 Check(new[]{"bridge-rebuilt","bridge-bound","bridge-reconnect","adapter-loop-resumed","adapter-root-moved"}.All(recovery.Contains),"recovery diagnostics persist typed transport and owner lifecycle events");
 collect.Accept("private-session",[new(14,"settings-input",Count:47)]);
 await log.FlushAsync(cancelled.Token);var focusEvents=string.Join("",(await log.SnapshotAsync()).Select(Encoding.UTF8.GetString));
 Check(focusEvents.Contains("settings-input")&&focusEvents.Contains("\"count\":47")&&!focusEvents.Contains("private-session"),"settings input diagnostics retain numeric focus flags without text or keys");
 collect.Accept("private-session",[new(15,"settings-background","PRIVATE_BACKGROUND_BODY",Count:126)]);
 await log.FlushAsync(cancelled.Token);var backgroundEvents=string.Join("",(await log.SnapshotAsync()).Select(Encoding.UTF8.GetString));
 Check(backgroundEvents.Contains("settings-background")&&backgroundEvents.Contains("\"count\":126")&&!backgroundEvents.Contains("PRIVATE_BACKGROUND_BODY"),"background diagnostics retain numeric compositor flags without page content");
 var sendStages=new[]{"send-close-request","send-close-confirmed","send-close-interrupted","send-close-timeout","send-close-retry","send-draft-open","send-draft-unavailable","send-draft-queued","send-unconfirmed"};
 collect.Accept("private-session",sendStages.Select((stage,i)=>new GameLogEvent(16+i,stage,"PRIVATE_DRAFT_BODY",Channel:"team",Count:2,ElapsedMs:300)).ToArray());
 await log.FlushAsync(cancelled.Token);var sendEvents=string.Join("",(await log.SnapshotAsync()).Select(Encoding.UTF8.GetString));
 Check(sendStages.All(sendEvents.Contains)&&!sendEvents.Contains("PRIVATE_DRAFT_BODY"),"native close and separate draft stages retain timing without editable text");
 for(var i=0;i<400;i++)log.Record("backend",LogKind.ConfigSaved,new(Count:i,Binding:"Ctrl+Enter"));await log.FlushAsync(cancelled.Token);
 var files=Directory.GetFiles(log.DirectoryPath,"*.jsonl");Check(files.Length<=3&&files.All(f=>new FileInfo(f).Length<=2048),"rolling files enforce count and byte budgets");
 var settings=config.Current with{LlmKey="KEY_NOT_FOR_EXPORT",AzureKey="KEY_NOT_FOR_EXPORT",Model="PRIVATE_MODEL",LlmBaseUrl="https://PRIVATE_HOST/v1",IncomingCustomLanguage="PRIVATE_CUSTOM_LANGUAGE"};
 var zip=await DiagnosticExport.Create(Path.Combine(temp,"logs-normal"),log,new{phase="test"},settings);
 using var package=ZipFile.OpenRead(zip);var content=string.Join('\n',package.Entries.Select(e=>{using var r=new StreamReader(e.Open());return r.ReadToEnd();}));
 Check(!content.Contains("KEY_NOT_FOR_EXPORT")&&!content.Contains("PRIVATE_MODEL")&&!content.Contains("PRIVATE_HOST")&&!content.Contains("PRIVATE_CUSTOM_LANGUAGE"),"export config whitelist strips keys, model, URL and free-form language");
 Check(package.Entries.Any(e=>e.FullName=="status.json")&&package.Entries.Any(e=>e.FullName.StartsWith("logs/")),"one package contains logs and current diagnostic status");
}
var blocked=Path.Combine(temp,"blocked");Directory.CreateDirectory(blocked);File.WriteAllText(Path.Combine(blocked,"logs"),"not a directory");
await using(var log=new DiagnosticLog(blocked,1024,2,8)){log.Record("backend",LogKind.Started);await log.FlushAsync(cancelled.Token);Check(!log.Status.Available&&log.Status.WriteFailures>0,"disk/path failure is reported without crashing the application");}
await using(var log=new DiagnosticLog(Path.Combine(temp,"overflow"),1024,2,1)){
 var clock=Stopwatch.StartNew();for(var i=0;i<50000;i++)log.Record("backend",LogKind.ConfigSaved,new(Count:i));clock.Stop();Check(log.Status.DroppedEvents>0,"full log queue counts drops instead of blocking producer");Console.WriteLine("producer50000EventsMs="+clock.ElapsedMilliseconds);await log.FlushAsync(cancelled.Token);
}
File.WriteAllText(Path.Combine(root,"artifacts","diagnostics-tests.json"),JsonSerializer.Serialize(new{passed,realApiUsed=false,realGameInputUsed=false}));Console.WriteLine("PASS TOTAL "+passed);
