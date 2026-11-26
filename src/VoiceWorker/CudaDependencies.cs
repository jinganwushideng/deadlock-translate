using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DeadlockTranslate;

namespace DeadlockVoiceWorker;

internal static partial class CudaDependencies
{
    public static void Load(string? privateDirectory)
    {
        if (!HasSupportedDevice() || !LoadDriverRuntime())
            throw new CudaDependencyException("driver", "未找到支持 CUDA 13 的 NVIDIA 显卡或驱动。");

        if (privateDirectory is not null && Directory.Exists(privateDirectory))
        {
            LoadPrivateLibraries(privateDirectory);
            return;
        }

        foreach (var library in CuBlasPackage.Libraries)
            if (!NativeLibrary.TryLoad(library.Name, out _))
                throw MissingLibraries();
    }

    private static void LoadPrivateLibraries(string directory)
    {
        var paths = CuBlasPackage.Libraries.Select(library => Path.Combine(directory, library.Name)).ToArray();
        var verified = new List<FileStream>(paths.Length);
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw MissingLibraries();
            for (var i = 0; i < paths.Length; i++)
            {
                if ((File.GetAttributes(paths[i]) & FileAttributes.ReparsePoint) != 0) throw MissingLibraries();
                var file = File.Open(paths[i], FileMode.Open, FileAccess.Read, FileShare.Read);
                verified.Add(file);
                var library = CuBlasPackage.Libraries[i];
                if (file.Length != library.Bytes || !Convert.ToHexString(SHA256.HashData(file)).Equals(library.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw MissingLibraries();
            }

            foreach (var path in paths)
                if (!NativeLibrary.TryLoad(path, out _))
                    throw new CudaDependencyException("driver", "CUDA 组件无法加载，请检查 NVIDIA 显卡驱动。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw MissingLibraries();
        }
        finally
        {
            foreach (var file in verified) file.Dispose();
        }
    }

    private static CudaDependencyException MissingLibraries() => new("missing", "缺少可用的 CUDA 13 cuBLAS 组件。");

    private static bool LoadDriverRuntime()
    {
        if (NativeLibrary.TryLoad("nvcudart_hybrid64.dll", out _)) return true;
        var driver = Path.Combine(Environment.SystemDirectory, "nvcuda.dll");
        var repository = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
        if (!File.Exists(driver) || !Directory.Exists(repository)) return false;
        var version = FileVersionInfo.GetVersionInfo(driver).FileVersion;
        var candidates = Directory.EnumerateDirectories(repository, "nv*.inf_amd64_*")
            .Select(directory => Path.Combine(directory, "nvcudart_hybrid64.dll"))
            .Where(File.Exists)
            .OrderByDescending(file => FileVersionInfo.GetVersionInfo(file).FileVersion == version);
        var loaded = candidates.FirstOrDefault(file => NativeLibrary.TryLoad(file, out _));
        if (loaded is null) return false;
        // Only this worker inherits the matched driver library search path.
        Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(loaded) + ";" + Environment.GetEnvironmentVariable("PATH"));
        return true;
    }

    private static bool HasSupportedDevice()
    {
        try
        {
            if (DriverVersion(out var version) != 0 || version < 13000 || Initialize(0) != 0 || DeviceCount(out var count) != 0)
                return false;
            // CUDA 13's cuBLAS requires compute capability 7.5 or newer.
            for (var device = 0; device < count; device++)
                if (DeviceAttribute(out var major, 75, device) == 0 && DeviceAttribute(out var minor, 76, device) == 0 &&
                    (major > 7 || major == 7 && minor >= 5)) return true;
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("nvcuda.dll", EntryPoint = "cuDriverGetVersion")]
    private static partial int DriverVersion(out int version);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("nvcuda.dll", EntryPoint = "cuInit")]
    private static partial int Initialize(uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("nvcuda.dll", EntryPoint = "cuDeviceGetCount")]
    private static partial int DeviceCount(out int count);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("nvcuda.dll", EntryPoint = "cuDeviceGetAttribute")]
    private static partial int DeviceAttribute(out int value, int attribute, int device);
}

internal sealed class CudaDependencyException(string reason, string message) : InvalidOperationException(message)
{
    public string Reason { get; } = reason;
}
