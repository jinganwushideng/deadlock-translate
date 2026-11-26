namespace DeadlockTranslate;

internal static class CuBlasPackage
{
    public const string Version = "13.0.2.14";
    public const string ArchiveName = $"libcublas-windows-x86_64-{Version}-archive";
    // NVIDIA CUDA 13.0.1 redistribution manifest for Windows x64.
    public const string DownloadUrl = $"https://developer.download.nvidia.com/compute/cuda/redist/libcublas/windows-x86_64/{ArchiveName}.zip";
    public const long ArchiveBytes = 401229263;
    public const string ArchiveSha256 = "b03fd06fb14fa33e27fc441433fe74ccee9738b62c62a08d39960cb0ce9f1d14";

    // Load Lt first because it is a dependency of cuBLAS.
    public static readonly Library[] Libraries =
    [
        new("cublasLt64_13.dll", 478140016, "680561bdc5d8a8c921e9ae974e6b60a6608c9d9fca8c0fd125e9da08c5c33ff1"),
        new("cublas64_13.dll", 51776624, "b5846446f2a151ca24b74b8113d4ca32c52105ad89d4c2f1bc56a4ccffa9ea61")
    ];

    internal sealed record Library(string Name, long Bytes, string Sha256);
}
