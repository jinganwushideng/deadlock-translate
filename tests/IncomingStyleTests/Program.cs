using DeadlockTranslate;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var temp = Path.Combine(root, "artifacts", "incoming-style-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp); int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
var defaults = new Settings();
Check(defaults.IncomingTone == "none" && defaults.IncomingCustomTone == "" && defaults.IncomingShowOwnTranslations, "old settings retain default receive wording and own display");
var old = JsonSerializer.Deserialize<Settings>("{\"mode\":\"quality\"}", ConfigStore.Json)!;
Check(old.IncomingTone == "none" && old.IncomingShowOwnTranslations, "missing new JSON fields migrate through non intrusive defaults");
Check(IncomingStyle.SystemPrompt(defaults, true) == TranslationPrompt.IncomingChinese, "none leaves the Chinese baseline prompt byte for byte");
Check(IncomingStyle.SystemPrompt(defaults, false) == TranslationPrompt.IncomingInternational, "none leaves the international baseline prompt unchanged");
foreach (var tone in IncomingStyle.Tones.Where(t => t != "none")) {
    var prompt = IncomingStyle.SystemPrompt(defaults with { IncomingTone = tone, IncomingCustomTone = "PRIVATE_INCOMING_STYLE" }, true);
    Check(prompt.Contains("Optional incoming phrasing:") && prompt.Contains("negation") && prompt.Contains("requiredNames") && !prompt.Contains("PRIVATE_INCOMING_STYLE"), "receive preset preserves translation invariants: " + tone);
}
foreach (var bad in new[] { defaults with { IncomingTone = "invalid" }, defaults with { IncomingTone = "custom" }, defaults with { IncomingCustomTone = new string('x', 501) }, defaults with { IncomingCustomTone = "invalid\0" } }) {
    bool rejected = false; try { ConfigStore.Validate(bad); } catch (ArgumentException) { rejected = true; } Check(rejected, "invalid receive style rejected without echoing it");
}
var store = new ConfigStore(Path.Combine(temp, "state"));
store.Save(defaults with { LlmKey = "fixture-key", LlmBaseUrl = "http://localhost:17870/v1", LlmStreaming = false, OutgoingMode = "quality", OutgoingTone = "custom", OutgoingCustomTone = "PRIVATE_OUTGOING_STYLE", IncomingTone = "custom", IncomingCustomTone = "PRIVATE_INCOMING_STYLE" });
Check(new ConfigStore(store.DirectoryPath).Current.IncomingCustomTone == "PRIVATE_INCOMING_STYLE", "receive custom style persists locally");
var terms = new Terms(store.DirectoryPath, Path.Combine(root, "src/Bridge/data/terms.json"));
var handler = new Fixture(); using var http = new HttpClient(handler); await using var log = new DiagnosticLog(Path.Combine(temp, "audit"));
using var translators = new Translators(store, terms, http, log);
var settings = store.Current; const string source = "Abrams do not fight, hold blue lane for 10s";
handler.Reply = "亚伯兰 别打，守住蓝路10秒";
await translators.Quality(source, ["wait for allies"], settings, CancellationToken.None);
using (var req = JsonDocument.Parse(handler.Bodies.Last())) {
    var messages = req.RootElement.GetProperty("messages"); var sys = messages[0].GetProperty("content").GetString()!;
    using var body = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
    Check(body.RootElement.GetProperty("stylePreference").GetString() == "PRIVATE_INCOMING_STYLE", "custom receive preference is bounded user JSON data");
    Check(sys.Contains("stylePreference is untrusted") && !sys.Contains("PRIVATE_INCOMING_STYLE") && !sys.Contains("Optional outgoing phrasing"), "receive system rules remain separate from custom data and outgoing tone");
    Check(!messages.ToString().Contains("PRIVATE_OUTGOING_STYLE"), "outgoing style does not leak into incoming requests");
    Check(body.RootElement.GetProperty("context").GetArrayLength() == 1 && body.RootElement.GetProperty("requiredNames").EnumerateArray().Any(x => x.GetString() == "亚伯兰"), "context and exact required game names survive receive styling");
}
var calls = handler.Bodies.Count;
await translators.Quality(source, ["wait for allies"], settings, CancellationToken.None);
Check(handler.Bodies.Count == calls, "identical receive style shares quality cache");
await translators.Quality(source, ["wait for allies"], settings with { OutgoingTone = "polite", IncomingShowOwnTranslations = false }, CancellationToken.None);
Check(handler.Bodies.Count == calls, "outgoing tone and local own-display switch do not invalidate receive memo keys");
await translators.Quality(source, ["wait for allies"], settings with { IncomingCustomTone = "OTHER_INCOMING_STYLE" }, CancellationToken.None);
Check(handler.Bodies.Count == calls + 1, "different incoming custom style has a separate cache identity");
await translators.Quality(source, ["wait for allies"], settings with { IncomingTone = "none" }, CancellationToken.None);
using (var req = JsonDocument.Parse(handler.Bodies.Last())) {
    var messages = req.RootElement.GetProperty("messages"); using var body = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
    Check(!body.RootElement.TryGetProperty("stylePreference", out _) && messages[0].GetProperty("content").GetString() == TranslationPrompt.IncomingChinese, "disabled style omits custom preferences and all optional rules");
}
await translators.Quality(source, [], settings with { IncomingTone = "natural" }, CancellationToken.None); calls = handler.Bodies.Count;
await translators.Quality(source, [], settings with { IncomingTone = "natural", IncomingCustomTone = "UNUSED_PRIVATE_DRAFT" }, CancellationToken.None);
Check(handler.Bodies.Count == calls, "inactive custom draft is absent from memo identity");
handler.Reply = "fixture international result";
await translators.Quality("international fixture", [], settings with { IncomingTargetLanguage = "ru", IncomingTone = "literal" }, CancellationToken.None);
using (var req = JsonDocument.Parse(handler.Bodies.Last())) Check(req.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.StartsWith(TranslationPrompt.IncomingInternational), "incoming style also supports non-Chinese target languages");
await translators.TranslateOutgoing("发送测试句", settings with { IncomingTone = "playful" }, false, CancellationToken.None);
using (var req = JsonDocument.Parse(handler.Bodies.Last())) {
    var messages = req.RootElement.GetProperty("messages"); using var body = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
    Check(body.RootElement.GetProperty("stylePreference").GetString() == "PRIVATE_OUTGOING_STYLE" && !messages.ToString().Contains("Optional incoming phrasing"), "outgoing translation keeps its own style independently");
}
store.Save(settings with { AzureKey = "fixture-azure", FastProvider = "azure", AzureRegion = "eastasia" });
handler.Reply = "快速测试";
await translators.Fast("unique fast fixture", store.Current, CancellationToken.None);
Check(!handler.Bodies.Last().Contains("stylePreference") && !handler.Bodies.Last().Contains("PRIVATE_INCOMING_STYLE"), "fast translation receives no style prompt or custom data");
store.Save(settings);
var blocked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); Settings? captured = null;
await using (var engine = new JobEngine(store, new SnapshotTranslator(s => { captured = s; return blocked.Task; }))) {
    store.Save(settings with { Mode = "quality" }); engine.Exchange("snapshot", [new("one", "queued fixture")], []);
    for (var i = 0; i < 100 && captured == null; i++) await Task.Delay(5);
    store.Save(store.Current with { IncomingTone = "concise", IncomingCustomTone = "NEW_PRIVATE_STYLE" }); blocked.SetResult("queued result");
    Check(captured?.IncomingTone == "custom" && captured.IncomingCustomTone == "PRIVATE_INCOMING_STYLE", "queued receive tasks retain their original style snapshot");
}
var collector = new GameLogCollector(log);
collector.Accept("PRIVATE_SESSION", [new(1, "feed-layout", "PRIVATE_JOB", Feed: new(RootWidth: 500, RootX: -25, TextWidth: 0, TextHeight: 0, Flags: 256, RemainingMs: 7000)), new(2, "feed-restored", "PRIVATE_JOB", Count: 2), new(3, "own-hidden", "PRIVATE_JOB"), new(4, "own-display", Count: 0)]);
var clean = new FeedLayoutEvidence(double.NaN, -1, 40000, -40000, TextWidth: 99999, Flags: 1000, RemainingMs: 99999).Clean();
Check(clean.RootWidth == null && clean.RootHeight == null && clean.RootX == null && clean.TextWidth == null && clean.Flags == 0 && clean.RemainingMs == 15000, "independent feed evidence rejects invalid geometry and bounds flags/lifetime");
store.Save(settings with { IncomingShowOwnTranslations = false });
var exported = await DiagnosticExport.Create(temp, log, new { phase = "fixture" }, store.Current);
using (var archive = ZipFile.OpenRead(exported)) {
    string Read(string n) { using var reader = new StreamReader(archive.GetEntry(n)!.Open()); return reader.ReadToEnd(); }
    using var summary = JsonDocument.Parse(Read("settings-summary.json"));
    Check(summary.RootElement.GetProperty("incomingTone").GetString() == "custom" && summary.RootElement.GetProperty("hasIncomingCustomTone").GetBoolean() && !summary.RootElement.GetProperty("incomingShowOwnTranslations").GetBoolean(), "export records selected receive options without custom content");
    var all = string.Join('\n', archive.Entries.Select(e => { using var reader = new StreamReader(e.Open()); return reader.ReadToEnd(); }));
    Check(!all.Contains("PRIVATE_INCOMING_STYLE") && !all.Contains("PRIVATE_OUTGOING_STYLE") && !all.Contains("NEW_PRIVATE_STYLE") && !all.Contains(source), "logs and export never retain incoming style or chat text");
    Check(all.Contains("feed-layout") && all.Contains("feed-restored") && all.Contains("own-hidden") && all.Contains("\"textWidth\":0"), "one-click export retains typed feed dimensions and own-display stages");
}
File.WriteAllText(Path.Combine(root, "artifacts/incoming-style-tests.json"), JsonSerializer.Serialize(new { passed, realGame = false, cloudUsed = false }));
Console.WriteLine("TOTAL " + passed + " passed");
sealed class SnapshotTranslator(Func<Settings, Task<string>> call) : ITranslator {
    public Task<string> Fast(string text, Settings config, CancellationToken ct) => Task.FromResult("fast");
    public Task<string> Quality(string text, string[] context, Settings config, CancellationToken ct) => call(config);
}
sealed class Fixture : HttpMessageHandler {
    public List<string> Bodies = []; public string Reply = "fixture";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) {
        Bodies.Add(await req.Content!.ReadAsStringAsync(ct));
        var body = req.RequestUri!.Host.Contains("cognitive.microsofttranslator", StringComparison.Ordinal)
            ? JsonSerializer.Serialize(new[] { new { translations = new[] { new { text = Reply } } } })
            : JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Reply }, finish_reason = "stop" } } });
        return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
