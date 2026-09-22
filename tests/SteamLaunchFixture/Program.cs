using System.Diagnostics;
using System.Text.Json;
var output=Environment.GetEnvironmentVariable("DLT_TEST_OUTPUT")!;
using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(2)};
var ready=(await http.GetAsync(Environment.GetEnvironmentVariable("DLT_TEST_URL")+"/health")).IsSuccessStatusCode;
using var process=Process.GetCurrentProcess();
File.WriteAllText(output,JsonSerializer.Serialize(new{arguments=args,workingDirectory=Environment.CurrentDirectory,steamAppId=Environment.GetEnvironmentVariable("SteamAppId"),ready,priority=process.PriorityClass.ToString(),pid=process.Id}));
await Task.Delay(4500);
