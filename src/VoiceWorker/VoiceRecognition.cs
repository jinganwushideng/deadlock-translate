using System.Diagnostics;
using System.Text;
using DeadlockVoiceLab.Asr;
using DeadlockVoiceLab.Audio;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace DeadlockVoiceWorker;

internal sealed record Transcript(string Text, string Language, double Milliseconds,
    bool Trusted, float? Probability, float? NoSpeechProbability);

internal sealed class VoiceRecognition : IDisposable
{
    private readonly WhisperFactory factory;
    private readonly bool shortPreviewContext;
    private string? executionBackend;

    public string? ExecutionBackend => Volatile.Read(ref executionBackend);

    public VoiceRecognition(string path, string backend, string? cudaDirectory = null, bool disableFlashAttention = false,
        bool reuseRuntime = false)
    {
        shortPreviewContext = backend != "vulkan";
        if (!reuseRuntime)
        {
            if (backend == "cuda") CudaDependencies.Load(cudaDirectory);
            RuntimeOptions.RuntimeLibraryOrder = backend switch
            {
                "cpu" => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
                "cuda" => [RuntimeLibrary.Cuda],
                "vulkan" => [RuntimeLibrary.Vulkan],
                _ => throw new ArgumentException("未知的语音识别设备。")
            };
        }
        using var logger = LogProvider.AddLogger((_, message) => ObserveBackend(message));
        factory = WhisperFactory.FromPath(path, new WhisperFactoryOptions
        {
            UseGpu = backend != "cpu",
            UseFlashAttention = backend == "vulkan" && !disableFlashAttention
        });
    }

    private void ObserveBackend(string? message)
    {
        if (message is null || !message.Contains("whisper_backend_init_gpu:", StringComparison.Ordinal)) return;
        if (message.Contains("no GPU found", StringComparison.Ordinal) || message.Contains("failed to initialize", StringComparison.Ordinal))
            Volatile.Write(ref executionBackend, "cpu");
        else if (message.Contains("using Vulkan", StringComparison.OrdinalIgnoreCase))
            Volatile.Write(ref executionBackend, "vulkan");
        else if (message.Contains("using CUDA", StringComparison.OrdinalIgnoreCase))
            Volatile.Write(ref executionBackend, "cuda");
    }

    public async Task<Transcript> Read(float[] samples, string language, CancellationToken ct,
        bool preview = false, bool teammateCaption = false, bool recovery = false)
    {
        var timer = Stopwatch.StartNew();
        // One inference runs at a time. Observe the backend only for this model.
        using var logger = LogProvider.AddLogger((_, message) => ObserveBackend(message));
        bool effectLike = VoiceAudioEvidence.IsEffectLike(samples);
        if (samples.Length < 9600) Array.Resize(ref samples, 9600);
        var builder = factory.CreateBuilder().WithNoContext().WithThreads(4)
            .WithNoSpeechThreshold(0.6f).WithMaxTokensPerSegment(preview ? 24 : 96).WithProbabilities();
        // Weak or noisy captions should leave the engine free for the next callout.
        if (teammateCaption) builder.WithTemperatureInc(0);
        if (preview && (shortPreviewContext || teammateCaption))
        {
            builder.WithSingleSegment().WithTemperatureInc(0);
            if (shortPreviewContext || teammateCaption && ExecutionBackend == "cpu")
            {
                // Whisper's encoder uses 20 ms frames. Leave room beyond the newest syllable.
                int audioFrames = (samples.Length + 319) / 320;
                builder.WithAudioContextSize(Math.Max(64, audioFrames + 32));
            }
        }
        else if (recovery && samples.Length <= 80_000)
        {
            // Decode the complete short callout with a smaller encoder window.
            // Keep 640 ms of right context and round up to an aligned frame count.
            int frames = (samples.Length + 319) / 320 + 32;
            builder.WithAudioContextSize(Math.Max(128, (frames + 31) / 32 * 32));
        }
        else if (teammateCaption && ExecutionBackend == "cpu" && samples.Length <= 80_000)
        {
            // Fixed context covers the complete caption segment plus a right margin.
            builder.WithAudioContextSize(256);
        }
        if (language == "auto") builder.WithLanguageDetection();
        else builder.WithLanguage(language);
        var text = new StringBuilder();
        var detectedLanguage = language;
        float? weakest = null, speechNoise = null;
        bool unclear = false;
        await using var processor = builder.Build();
        await foreach (var part in processor.ProcessAsync(samples, ct))
        {
            if (HallucinationFilter.ShouldDropSegment(part.Text, part.Probability, part.NoSpeechProbability, effectLike)) continue;
            if (float.IsFinite(part.Probability))
                weakest = Math.Min(weakest ?? 1, part.Probability);
            if (float.IsFinite(part.NoSpeechProbability))
                speechNoise = Math.Max(speechNoise ?? 0, part.NoSpeechProbability);
            if (teammateCaption && !CaptionConfidence.Trust(part.Text, part.Probability, part.NoSpeechProbability, preview))
                unclear = true;
            if (!string.IsNullOrEmpty(part.Language)) detectedLanguage = part.Language;
            if (!string.IsNullOrWhiteSpace(part.Text)) text.Append(' ').Append(part.Text.Trim());
        }
        var cleaned = HallucinationFilter.CleanAndFilter(text.ToString(), out var rejected);
        return new(rejected ? "" : cleaned, detectedLanguage, timer.Elapsed.TotalMilliseconds,
            !rejected && !unclear, weakest, speechNoise);
    }

    public void Dispose() => factory.Dispose();
}
