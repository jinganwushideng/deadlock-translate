using DeadlockTranslate;
using System.Text.Json;

var passed=0;void Check(bool ok,string name){if(!ok)throw new Exception("FAIL "+name);passed++;Console.WriteLine("PASS "+name);}
void Rejected(Action action,string name){try{action();throw new Exception("Expected rejection");}catch(ArgumentException){Check(true,name);}}
var now=100000L;using var service=new NativeSettingsEditor(()=>now);
string owner=new('a',32),other=new('b',32);
var input=new NativeEditorDraft(owner,"自定义接收风格","原文\n中文草稿",true,600);
var ticket=service.Create(input);Check(NativeSettingsEditor.ValidId(ticket.Id)&&ticket.State=="pending"&&ticket.Value==null,"opaque ticket contains no editable text");
Check(service.Read(ticket.Id,other)==null,"a different page owner cannot read the draft");
Check(service.Read(ticket.Id,owner)?.Value==null,"pending poll never exposes initial text");
var claim=service.Exchange(new(ticket.Id,"claim"));Check(claim?.State=="editing"&&claim.Value==input.Value&&claim.Multiline&&claim.MaxLength==600,"paired native claim preserves Unicode, lines and field bounds");
Check(service.Exchange(new(ticket.Id,"claim"))==claim,"duplicate claim replays unchanged metadata");
Check(service.Exchange(new(ticket.Id,"apply","按中文玩家口语翻译\n保留专名"))?.State=="applied","native confirm completes the local draft");
var result=service.Read(ticket.Id,owner);Check(result?.State=="applied"&&result.Value=="按中文玩家口语翻译\n保留专名","only the owning page receives the committed draft");
service.Exchange(new(ticket.Id,"apply","迟到的第二份文本"));Check(service.Read(ticket.Id,owner)?.Value==result?.Value,"late duplicate apply cannot replace the first result");
Check(!service.Cancel(ticket.Id,other)&&service.Read(ticket.Id,owner)?.Value==result?.Value,"a different owner cannot cancel or erase the result");
Check(service.Cancel(ticket.Id,owner)&&service.Read(ticket.Id,owner)?.Value==null,"acknowledgement removes retained result content");
var cancelled=service.Create(input);service.Exchange(new(cancelled.Id,"claim"));service.Exchange(new(cancelled.Id,"cancel"));service.Exchange(new(cancelled.Id,"apply","不能恢复的迟到文本"));
Check(service.Read(cancelled.Id,owner)?.State=="cancelled"&&service.Read(cancelled.Id,owner)?.Value==null,"cancelled native edits cannot later apply");
var pending=service.Create(input);now+=15001;Check(service.Read(pending.Id,owner)==null,"an unclaimed request expires after 15 seconds");
var active=service.Create(input);service.Exchange(new(active.Id,"claim"));now+=599999;Check(service.Read(active.Id,owner)?.State=="editing","active Unicode editing remains available for ten minutes");
now+=2;Check(service.Read(active.Id,owner)==null,"expired editing values are pruned");
Rejected(()=>service.Create(input with{Owner="invalid"}),"invalid page owner rejected");
Rejected(()=>service.Create(input with{Label="\0"}),"control characters in captions rejected");
Rejected(()=>service.Create(input with{MaxLength=4097}),"oversized field limits rejected");
Rejected(()=>service.Create(input with{Value=new string('x',601)}),"oversized current draft rejected");
Rejected(()=>service.Create(input with{Multiline=false}),"single-line fields reject newlines");
Rejected(()=>service.Exchange(new("bad-id","claim")),"invalid ticket cannot access memory");
Rejected(()=>service.Exchange(new(new string('c',48),"execute")),"unknown native operations rejected");
var bounded=service.Create(new(owner,"短词","原词",false,5));service.Exchange(new(bounded.Id,"claim"));
Rejected(()=>service.Exchange(new(bounded.Id,"apply","超过五个汉字的内容")),"native completion enforces the captured field bound");
Check(service.Read(bounded.Id,owner)?.State=="editing","rejected content leaves the editor available for correction");service.Cancel(bounded.Id,owner);
using var capacity=new NativeSettingsEditor(()=>now);
var slots=new List<NativeEditorView>();for(int i=0;i<16;i++)slots.Add(capacity.Create(new(i.ToString("x32"),"测试","",false,20)));
bool full=false;try{capacity.Create(new(new string('d',32),"测试","",false,20));}catch(InvalidOperationException){full=true;}
Check(full,"draft storage is bounded, including unacknowledged terminal results");
using var dispatch=new NativeSettingsEditor(()=>now);var dispatchTicket=dispatch.Create(new(owner,"原生测试","原有中文",false,20));
var correlated=dispatch.Dispatch(new(dispatchTicket.Id,"claim",Request:7),true);Check(correlated.Request==7&&correlated.State=="editing"&&correlated.Value=="原有中文","main game exchange preserves request correlation and native draft metadata");
var denied=dispatch.Dispatch(new(dispatchTicket.Id,"apply","不应写入",8),false);Check(denied.State=="error"&&dispatch.Read(dispatchTicket.Id,owner)?.State=="editing","unverified game/window requests return an editor error without mutation");
var invalid=dispatch.Dispatch(new(dispatchTicket.Id,"apply",new string('字',21),9),true);Check(invalid.State=="error"&&invalid.Request==9&&dispatch.Read(dispatchTicket.Id,owner)?.State=="editing","invalid editor payloads cannot fail the main heartbeat or discard an editable draft");
var applied=dispatch.Dispatch(new(dispatchTicket.Id,"apply","确认回填中文",10),true);Check(applied.State=="applied"&&applied.Request==10&&dispatch.Read(dispatchTicket.Id,owner)?.Value=="确认回填中文","main-channel completion remains an explicit page draft");
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));var report=new{passed,realGame=false,apiCalls=0,persistedDrafts=false};
File.WriteAllText(Path.Combine(root,"artifacts/native-editor-service-tests.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("TOTAL "+passed);
