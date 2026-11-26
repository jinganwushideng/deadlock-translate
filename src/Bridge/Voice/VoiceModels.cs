using System.Security.Cryptography;

namespace DeadlockTranslate;

internal sealed record VoiceModel(string Id, string Name, long Bytes, string Sha256)
{
    public string FileName => $"ggml-{Id}.bin";
}

internal sealed class VoiceModels(string directory) : IDisposable
{
    internal const string DraftModel = "base-q5_1";
    public static readonly VoiceModel[] Presets =
    [
        new(DraftModel, "Base Q5_1", 59707625, "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898"),
        new("small-q5_1", "Small Q5_1", 190085487, "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb"),
        new("medium-q5_0", "Medium Q5_0", 539212467, "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f"),
        new("large-v3-turbo-q5_0", "Large-v3-Turbo Q5_0", 574041195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2")
    ];

    private readonly object gate = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(20) };
    private CancellationTokenSource? download;
    private VoiceDownload snapshot = new("", 0, 0, false, null);

    public VoiceDownload Status() { lock (gate) return snapshot; }
    public bool Installed(VoiceModel model)
    {
        var path = new FileInfo(Path.Combine(directory, model.FileName));
        return path.Exists && path.Length == model.Bytes;
    }

    public async Task<string> Verify(VoiceOptions options, CancellationToken ct)
    {
        if (options.Model == "custom")
        {
            if (!File.Exists(options.CustomPath)) throw new IOException("未找到选中的本地模型。");
            return options.CustomPath;
        }
        return await VerifyPreset(options.Model, ct);
    }

    public async Task<string> VerifyPreset(string id, CancellationToken ct)
    {
        var model = Presets.Single(item => item.Id == id);
        var path = Path.Combine(directory, model.FileName);
        if (!Installed(model)) throw new IOException("请先下载选中的模型。");
        if (!await VerifyHash(path, model, ct)) throw new IOException("模型校验失败，请重新下载。");
        return path;
    }

    public void Start(string id, string mirror)
    {
        var model = Presets.FirstOrDefault(item => item.Id == id) ?? throw new ArgumentException("请选择预设模型。");
        var endpoint = mirror switch
        {
            "official" => "https://huggingface.co",
            "mirror" => "https://hf-mirror.com",
            _ => throw new ArgumentException("请选择模型下载来源。")
        };
        lock (gate)
        {
            if (download != null) throw new ArgumentException("已有模型正在下载。");
            download = new();
            snapshot = new(model.Id, 0, model.Bytes, true, null);
            _ = Fetch(model, endpoint, download);
        }
    }

    private async Task Fetch(VoiceModel model, string endpoint, CancellationTokenSource source)
    {
        var target = Path.Combine(directory, model.FileName);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(source.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        var ct = deadline.Token;
        long received = 0;
        string? error = null;
        try
        {
            Directory.CreateDirectory(directory);
            using var reply = await http.GetAsync(endpoint + "/ggerganov/whisper.cpp/resolve/main/" + model.FileName,
                HttpCompletionOption.ResponseHeadersRead, ct);
            reply.EnsureSuccessStatusCode();
            await using (var input = await reply.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = new byte[128 * 1024];
                long nextUpdate = 0;
                while (await input.ReadAsync(buffer, ct) is int size && size > 0)
                {
                    received += size;
                    if (received > model.Bytes) throw new IOException();
                    await output.WriteAsync(buffer.AsMemory(0, size), ct);
                    var now = Environment.TickCount64;
                    if (now < nextUpdate) continue;
                    nextUpdate = now + 250;
                    lock (gate) snapshot = snapshot with { Received = received };
                }
            }
            if (received != model.Bytes || !await VerifyHash(temporary, model, ct)) throw new IOException();
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target, true);
        }
        catch (OperationCanceledException) { error = source.IsCancellationRequested ? "已取消下载" : "模型下载超时"; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
        { error = "下载或校验失败，请检查网络、存储空间或更换下载来源。"; }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            lock (gate)
            {
                download = null;
                snapshot = new(model.Id, received, model.Bytes, false, error);
                source.Dispose();
            }
        }
    }

    private static async Task<bool> VerifyHash(string path, VoiceModel model, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).Equals(model.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public void Cancel() { lock (gate) download?.Cancel(); }
    public void Dispose() { Cancel(); http.Dispose(); }
}

internal sealed record VoiceDownload(string Model, long Received, long Total, bool Active, string? Error);
