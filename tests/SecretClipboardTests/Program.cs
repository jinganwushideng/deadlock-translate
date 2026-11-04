using DeadlockTranslate;
using System.Text.Json;
var passed=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
void Reject(string? value,string code){try{SecretClipboard.Validate(value);throw new Exception("Accepted invalid clipboard data");}catch(SecretClipboardException ex){Check(ex.Code==code&&!ex.Message.Contains("PRIVATE_MARKER"),"invalid clipboard content returns a bounded, content-free "+code+" message");}}
Check(SecretClipboard.ValidField("llmKey")&&SecretClipboard.ValidField("azureKey")&&!SecretClipboard.ValidField("model")&&!SecretClipboard.ValidField(null),"only the two intended password fields are accepted");
Check(SecretClipboard.Validate("  fixture_ABC-123/+==\r\n")=="fixture_ABC-123/+==","copied outer whitespace is trimmed without changing key punctuation or casing");
Check(SecretClipboard.Validate(new string('x',4096)).Length==4096,"maximum supported key fits the bounded clipboard path");
Reject(null,"clipboard-size");Reject(" \r\n\t ","clipboard-empty");Reject("PRIVATE_MARKER\nmore","clipboard-format");Reject("Bearer PRIVATE_MARKER","clipboard-format");Reject("PRIVATE_MARKER\0","clipboard-format");Reject("PRIVATE_MARKER中文","clipboard-format");Reject(new string('x',4097),"clipboard-format");Reject(new string('x',8193),"clipboard-size");
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
Directory.CreateDirectory(Path.Combine(root,"artifacts"));File.WriteAllText(Path.Combine(root,"artifacts/secret-clipboard-tests.json"),JsonSerializer.Serialize(new{passed,systemClipboardRead=false,realCredentialsUsed=false,cloudCalls=0}));
Console.WriteLine("TOTAL "+passed);
