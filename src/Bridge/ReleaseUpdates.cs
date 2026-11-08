using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeadlockTranslate;

internal sealed record ReleaseAsset(string Url, long Bytes, string? Digest);
internal sealed record UpdateRelease(string Version, string Summary, ReleaseAsset Installer, ReleaseAsset Checksum);
internal sealed record UpdateCache(DateTimeOffset? LastAttempt = null, DateTimeOffset? LastSuccess = null,
    DateTimeOffset? RetryAt = null, UpdateRelease? Release = null, string? Dismissed = null, string? Error = null, bool Automatic = true);
internal sealed record UpdateStatus(string CurrentVersion, bool Available, string? Version, string? Summary, long Size,
    DateTimeOffset? LastChecked, DateTimeOffset? RetryAt, string? CheckError, string Download, long Downloaded, string? DownloadError, bool Automatic);

internal sealed class ReleaseUpdates : IDisposable
{
    internal const long MaxInstallerBytes = 512L * 1024 * 1024;
    private const string Repository = "jinganwushideng/deadlock-translate";
    private static readonly Uri Latest = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private readonly string root;
    private readonly string current;
    private readonly HttpClient http;
    private readonly Func<DateTimeOffset> now;
    private readonly object gate = new();
    private CancellationTokenSource? downloadCancellation;
    private string download = "idle";
    private string? downloadError;
    private long downloaded;
    private string? downloadVersion;

    public ReleaseUpdates(string stateRoot, string current, HttpMessageHandler? transport = null, Func<DateTimeOffset>? clock = null)
    {
        root = Path.Combine(stateRoot, "release-updates");
        this.current = current;
        now = clock ?? (() => DateTimeOffset.UtcNow);
        http = new HttpClient(transport ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DeadlockTranslate/" + current);
    }

    internal static Version? ParseVersion(string? value)
    {
        if (value is null || !Regex.IsMatch(value, @"^v?\d+\.\d+\.\d+(?:\.\d+)?$", RegexOptions.CultureInvariant)) return null;
        return Version.TryParse(value.TrimStart('v'), out var version)
            ? new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision)) : null;
    }

    private static void NoLinks(string path)
    {
        for (var full = Path.GetFullPath(path); full is not null; full = Path.GetDirectoryName(full))
            if ((File.Exists(full) || Directory.Exists(full)) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("更新缓存路径包含链接。");
    }

    private string CachePath => Path.Combine(root, "state.json");
    private UpdateCache Read()
    {
        NoLinks(CachePath);
        try { return File.Exists(CachePath) && new FileInfo(CachePath).Length <= 65536
            ? JsonSerializer.Deserialize<UpdateCache>(File.ReadAllBytes(CachePath)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    private FileStream? TryLock(string name)
    {
        var path = Path.Combine(root, name);
        NoLinks(path);
        Directory.CreateDirectory(root);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    private void Save(UpdateCache state)
    {
        var temp = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
        try {
            using (var stream = new FileStream(temp, FileMode.CreateNew)) {
                JsonSerializer.Serialize(stream, state);
                stream.Flush(true);
            }
            File.Move(temp, CachePath, true);
        } finally { File.Delete(temp); }
    }

    private bool IsNew(UpdateRelease? release) => ParseVersion(release?.Version) is { } next && next > ParseVersion(current);
    private string PackagePath(string version) => Path.Combine(root, $"DeadlockTranslate-Setup-{version}.exe");
    private string HashPath(string version) => PackagePath(version) + ".sha256";
    public UpdateStatus Status()
    {
        var cache = Read();
        var release = IsNew(cache.Release) ? cache.Release : null;
        lock (gate) {
            var state = downloadVersion == release?.Version ? download : "idle";
            if (release != null && state == "idle" && File.Exists(PackagePath(release.Version)) && File.Exists(HashPath(release.Version))) state = "ready";
            return new(current, release != null && cache.Dismissed != release.Version, release?.Version, release?.Summary,
                release?.Installer.Bytes ?? 0, cache.LastSuccess, cache.RetryAt, cache.Error,
                state, downloadVersion == release?.Version ? downloaded : 0, downloadVersion == release?.Version ? downloadError : null, cache.Automatic);
        }
    }

    public async Task<UpdateStatus> Check(bool automatic, CancellationToken token)
    {
        using var held = TryLock("check.lock");
        if (held == null) return Status();
        var cache = Read();
        var utc = now();
        if (automatic && !cache.Automatic) return Status();
        if (cache.RetryAt > utc || (cache.LastAttempt is { } last && utc >= last &&
            utc - last < (automatic ? TimeSpan.FromHours(24) : TimeSpan.FromSeconds(30)))) return Status();
        Save(cache = cache with { LastAttempt = utc });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try {
            using var response = await http.GetAsync(Latest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.Forbidden) {
                var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - utc) ?? TimeSpan.FromMinutes(30);
                Save(cache with { Error = "GitHub 暂时限制检查频率。", RetryAt = utc.AddSeconds(Math.Clamp(retry.TotalSeconds, 60, 86400)) });
                return Status();
            }
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await ReadLimited(response.Content, 256 * 1024, timeout.Token));
            var release = ReadRelease(doc.RootElement);
            Save(cache with { LastSuccess = utc, RetryAt = null, Release = release, Error = null });
        } catch (Exception e) when (!token.IsCancellationRequested && e is HttpRequestException or IOException or JsonException or OperationCanceledException) {
            Save(cache with { Error = "无法读取 GitHub 更新，可以稍后重试。", RetryAt = utc.AddMinutes(2) });
        }
        return Status();
    }

    private static async Task<byte[]> ReadLimited(HttpContent content, int limit, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true) {
            int count = await stream.ReadAsync(bytes, ct);
            if (count == 0) return buffer.ToArray();
            if (buffer.Length + count > limit) throw new IOException("更新响应超过大小限制。");
            buffer.Write(bytes, 0, count);
        }
    }

    internal static UpdateRelease? ReadRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False ||
            ParseVersion(tag.GetString()) == null || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        var version = tag.GetString()!.TrimStart('v');
        string file = $"DeadlockTranslate-Setup-{version}.exe";
        ReleaseAsset? Pick(string name, long limit) {
            foreach (var a in assets.EnumerateArray()) {
                if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || n.GetString() != name) continue;
                if (!a.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String ||
                    url.GetString() != $"https://github.com/{Repository}/releases/download/v{version}/{name}" ||
                    !a.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var count) || count <= 0 || count > limit) return null;
                var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (digest != null && !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$")) return null;
                return new(url.GetString()!, count, digest?[7..].ToLowerInvariant());
            }
            return null;
        }
        var installer = Pick(file, MaxInstallerBytes);
        var checksum = Pick(file + ".sha256", 4096);
        if (installer == null || checksum == null) return null;
        var summary = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
        return new(version, summary[..Math.Min(summary.Length, 1600)], installer, checksum);
    }

    private static bool IsAllowedAssetHost(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com";
    private async Task<HttpResponseMessage> GetAsset(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        for (int i = 0; i < 5; i++) {
            if (!IsAllowedAssetHost(uri)) throw new IOException("更新文件来源不符合校验规则。");
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location) {
                uri = new Uri(uri, location);response.Dispose();continue;
            }
            if (!response.IsSuccessStatusCode) { response.Dispose();throw new IOException("更新文件暂时无法下载。"); }
            return response;
        }
        throw new IOException("更新文件重定向次数过多。");
    }

    private static bool ValidReleaseUrls(UpdateRelease release) => ParseVersion(release.Version) != null &&
        release.Installer.Url == $"https://github.com/{Repository}/releases/download/v{release.Version}/DeadlockTranslate-Setup-{release.Version}.exe" &&
        release.Checksum.Url == release.Installer.Url + ".sha256";

    public void Dismiss()
    {
        using var held = TryLock("check.lock") ?? throw new IOException("正在检查更新，请稍后重试。");
        var cache = Read();Save(cache with { Dismissed = cache.Release?.Version });
    }

    public void SetAutomatic(bool enabled)
    {
        using var held = TryLock("check.lock") ?? throw new IOException("正在检查更新，请稍后重试。");
        Save(Read() with { Automatic = enabled });
    }

    public async Task RunDaily(CancellationToken ct)
    {
        try {
            await Task.Delay(TimeSpan.FromSeconds(20),ct);
            while (!ct.IsCancellationRequested) {
                try { await Check(true,ct); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                await Task.Delay(TimeSpan.FromHours(1),ct);
            }
        } catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
    }

    public void StartDownload()
    {
        var release = Read().Release;
        if (!IsNew(release) || !ValidReleaseUrls(release!)) throw new IOException("请先检查可用的正式更新。");
        lock (gate) {
            if (downloadCancellation != null) return;
            var held = TryLock("download.lock") ?? throw new IOException("另一窗口正在下载更新。");
            var operation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            downloadCancellation = operation;downloadVersion = release!.Version;download = "downloading";downloadError = null;downloaded = 0;
            _ = Task.Run(() => Download(release, held, operation));
        }
    }

    private async Task Download(UpdateRelease release, FileStream held, CancellationTokenSource operation)
    {
        var ct = operation.Token;
        var path = PackagePath(release.Version);
        var part = path + ".part";
        try {
            NoLinks(path);NoLinks(part);NoLinks(HashPath(release.Version));
            foreach (var expired in Directory.EnumerateFiles(root, "*.part")) {
                NoLinks(expired);File.Delete(expired);
            }
            using var sum = await GetAsset(release.Checksum.Url, ct);
            var sumBytes = await ReadLimited(sum.Content, 4096, ct);
            var match = Regex.Match(System.Text.Encoding.ASCII.GetString(sumBytes).Trim(), @"^([a-fA-F0-9]{64})(?:[ \t]+\*?([^\r\n]+))?$");
            var expected = match.Groups[1].Value.ToLowerInvariant();
            if (!match.Success || (match.Groups[2].Success && match.Groups[2].Value != Path.GetFileName(path)) ||
                (release.Installer.Digest != null && release.Installer.Digest != expected) ||
                (release.Checksum.Digest != null && !HashEquals(SHA256.HashData(sumBytes), release.Checksum.Digest))) throw new IOException("更新校验清单不匹配。");
            using var response = await GetAsset(release.Installer.Url, ct);
            if (response.Content.Headers.ContentLength is long length && length != release.Installer.Bytes) throw new IOException("更新文件大小不符。");
            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true)) {
                var bytes = new byte[65536];
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0;
                while (true) {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(TimeSpan.FromSeconds(30));
                    int count = await stream.ReadAsync(bytes, idle.Token);
                    if (count == 0) break;
                    total += count;
                    if (total > release.Installer.Bytes || total > MaxInstallerBytes) throw new IOException("更新文件超过预期大小。");
                    hash.AppendData(bytes, 0, count);await file.WriteAsync(bytes.AsMemory(0, count), ct);
                    lock (gate) downloaded = total;
                }
                if (total != release.Installer.Bytes || !HashEquals(hash.GetHashAndReset(), expected)) throw new IOException("安装包 SHA-256 或大小校验失败。");
                file.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(part, path, true);File.WriteAllText(HashPath(release.Version), expected);
            lock (gate) download = "ready";
        } catch (OperationCanceledException) {
            lock (gate) { download = "cancelled";downloadError = "下载已取消或超时，可以重试。"; }
        } catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException or ArgumentException) {
            lock (gate) { download = "failed";downloadError = "下载或校验未完成，请重试。"; }
        } finally {
            try { NoLinks(part);File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            held.Dispose();lock (gate) { downloadCancellation = null;operation.Dispose(); }
        }
    }

    internal static bool HashEquals(byte[] hash, string expected) => Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    public void CancelDownload() { lock (gate) downloadCancellation?.Cancel(); }

    public static bool GameRunning()
    {
        foreach (var name in new[] { "deadlock", "citadel" }) {
            var matches = Process.GetProcessesByName(name);
            foreach (var process in matches) process.Dispose();
            if (matches.Length > 0) return true;
        }
        return false;
    }

    public async Task StartInstaller(CancellationToken ct, bool waitPrevious = false)
    {
        if (GameRunning()) throw new IOException("请先正常退出 Deadlock，再打开已下载的安装器。");
        var release = Read().Release;
        if (!IsNew(release) || !ValidReleaseUrls(release!)) throw new IOException("请先下载可用的正式更新。");
        using var held = TryLock("download.lock") ?? throw new IOException("下载仍在进行。");
        var path = PackagePath(release!.Version);
        var checksum = HashPath(release.Version);
        NoLinks(path);NoLinks(checksum);
        if (!File.Exists(checksum) || new FileInfo(checksum).Length != 64) throw new IOException("请重新下载并校验安装器。");
        var expected = await File.ReadAllTextAsync(checksum, ct);
        if (release.Installer.Digest is { } digest && digest != expected) throw new IOException("已下载的安装器与正式发布不一致。");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != release.Installer.Bytes || !HashEquals(await SHA256.HashDataAsync(file, ct), expected)) throw new IOException("已下载的安装器校验失败，请重新下载。");
        if (GameRunning()) throw new IOException("Deadlock 仍在运行。");
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Arguments = waitPrevious ? "--wait-previous" : "" });
    }

    public void Dispose() { CancelDownload();http.Dispose(); }
}

internal sealed record UpdatePreference(bool Enabled);
