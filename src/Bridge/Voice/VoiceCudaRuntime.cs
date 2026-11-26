using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeadlockTranslate;

internal sealed record VoiceCudaStatus(string Availability = "unchecked", string? Phase = null,
    long Received = 0, long Total = CuBlasPackage.ArchiveBytes, string? Error = null);

internal sealed class VoiceCudaRuntime(string root, string worker, Action onInstalled) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private VoiceCudaStatus status = new();
    private Task? probe, transfer;
    private CancellationTokenSource? downloading;

    public string DirectoryPath { get; } = Path.Combine(root, CuBlasPackage.Version);

    public VoiceCudaStatus Status(bool check)
    {
        lock (gate)
        {
            if (check && status.Availability == "unchecked" && !shutdown.IsCancellationRequested)
            {
                status = status with { Availability = "checking" };
                probe = Task.Run(Check);
            }
            return status;
        }
    }

    public void ReportDependency(string? reason)
    {
        if (reason is not ("missing" or "driver")) return;
        lock (gate)
            if (downloading is null) status = status with { Availability = reason };
    }

    public void Start()
    {
        lock (gate)
        {
            if (downloading is not null) return;
            if (status.Availability != "missing" || shutdown.IsCancellationRequested)
                throw new ArgumentException("请先检测 CUDA 组件。仅在缺少 cuBLAS 时提供下载。");
            var source = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            downloading = source;
            status = status with { Phase = "downloading", Received = 0, Error = null };
            transfer = Task.Run(() => Install(source));
        }
    }

    public void Cancel() { lock (gate) downloading?.Cancel(); }

    private async Task Check()
    {
        var result = await Probe(DirectoryPath, shutdown.Token);
        lock (gate)
            if (status.Availability == "checking") status = status with { Availability = result };
    }

    private async Task<string> Probe(string directory, CancellationToken ct)
    {
        var start = new ProcessStartInfo(worker)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        start.ArgumentList.Add("--check-cuda");
        start.ArgumentList.Add(directory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var process = Process.Start(start);
            if (process is null) return "unavailable";
            try
            {
                var line = await process.StandardOutput.ReadLineAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                if (process.ExitCode != 0 || string.IsNullOrEmpty(line)) return "unavailable";
                using var reply = JsonDocument.Parse(line);
                var result = reply.RootElement.GetProperty("cuda").GetString();
                return result is "ready" or "missing" or "driver" ? result : "unavailable";
            }
            finally
            {
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or OperationCanceledException or JsonException or KeyNotFoundException)
        {
            return "unavailable";
        }
    }

    private async Task Install(CancellationTokenSource source)
    {
        var staging = Path.Combine(root, "download-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(staging, "cublas.zip");
        var files = Path.Combine(staging, "files");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(source.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        var ct = deadline.Token;
        string? error = null;
        string availability = "missing";
        try
        {
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException();
            Directory.CreateDirectory(files);
            await Download(archivePath, ct);
            lock (gate) status = status with { Phase = "verifying", Received = CuBlasPackage.ArchiveBytes };
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                foreach (var library in CuBlasPackage.Libraries)
                {
                    var entry = archive.GetEntry($"{CuBlasPackage.ArchiveName}/bin/x64/{library.Name}");
                    if (entry is null || entry.Length != library.Bytes) throw new InvalidDataException();
                    await using var original = entry.Open();
                    await using var destination = File.Create(Path.Combine(files, library.Name));
                    await original.CopyToAsync(destination, ct);
                }
            }

            // The worker verifies each DLL hash and loads only the staged pair.
            var stagedResult = await Probe(files, ct);
            ct.ThrowIfCancellationRequested();
            if (stagedResult == "ready")
            {
                Publish(files, Path.Combine(staging, "previous"));
                availability = "ready";
            }
            else
            {
                error = "CUDA 组件验证未通过。请检查 NVIDIA 驱动后重试。";
            }
        }
        catch (OperationCanceledException) { error = source.IsCancellationRequested ? "已取消 CUDA 下载。" : "CUDA 下载超时，请重试。"; }
        catch (InvalidDataException) { error = "CUDA 下载校验失败，请重新下载。"; }
        catch (HttpRequestException) { error = "无法从 NVIDIA 下载 CUDA 组件，请检查网络后重试。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { error = "CUDA 下载或安装失败，请检查网络、存储空间和权限后重试。"; }
        finally
        {
            TryRemoveFiles(files);
            try { File.Delete(archivePath); Directory.Delete(staging); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            lock (gate)
            {
                downloading = null;
                status = status with { Availability = availability, Phase = null, Error = error };
                source.Dispose();
            }
        }
        if (availability == "ready") onInstalled();
    }

    private async Task Download(string path, CancellationToken ct)
    {
        using var reply = await http.GetAsync(CuBlasPackage.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        reply.EnsureSuccessStatusCode();
        if (reply.Content.Headers.ContentLength is long size && size != CuBlasPackage.ArchiveBytes)
            throw new InvalidDataException();
        await using var input = await reply.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long received = 0, nextUpdate = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            received += read;
            if (received > CuBlasPackage.ArchiveBytes) throw new InvalidDataException();
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            var now = Environment.TickCount64;
            if (now < nextUpdate) continue;
            nextUpdate = now + 250;
            lock (gate) status = status with { Received = received };
        }
        if (received != CuBlasPackage.ArchiveBytes ||
            !Convert.ToHexString(hash.GetHashAndReset()).Equals(CuBlasPackage.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
    }

    private void Publish(string staging, string previous)
    {
        if (Directory.Exists(DirectoryPath))
        {
            if ((File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0) throw new IOException();
            Directory.Move(DirectoryPath, previous);
        }
        try { Directory.Move(staging, DirectoryPath); }
        catch
        {
            if (Directory.Exists(previous)) Directory.Move(previous, DirectoryPath);
            throw;
        }
        TryRemoveFiles(previous);
    }

    private static void TryRemoveFiles(string directory)
    {
        try
        {
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var library in CuBlasPackage.Libraries) File.Delete(Path.Combine(directory, library.Name));
            Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Task? check, pending;
        lock (gate) { shutdown.Cancel(); check = probe; pending = transfer; }
        if (check is not null) await check;
        if (pending is not null) await pending;
        http.Dispose();
        shutdown.Dispose();
    }
}
