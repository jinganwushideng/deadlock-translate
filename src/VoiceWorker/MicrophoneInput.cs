using System.Text;
using System.Threading.Channels;
using DeadlockVoiceLab.Audio;

namespace DeadlockVoiceWorker;

internal sealed record MicCommand(string Id, string Action, string Device, string Language);
internal sealed record MicWork(string Id, string Language, float[]? Samples, long? PreviewSegment = null);

internal sealed class MicrophoneInput(Action<object> output, Action wake, int previewIntervalMs = 600) : IAsyncDisposable
{
    private const int FirstPreviewSamples = AudioDspPipeline.OutputSampleRate * 600 / 1000;
    private readonly object gate = new();
    private readonly WasapiProcessCapture capture = new();
    private readonly AudioDspPipeline dsp = new();
    private readonly VoiceSegmenter segmenter = new(SegmenterOptions.TeammateVoice with { MaxSegmentSeconds = 3 }, false);
    private readonly Channel<MicWork> pending = Channel.CreateBounded<MicWork>(128);
    private MicCommand? current;
    private CancellationTokenSource? lease;
    private string? retired;
    private bool recording;
    private readonly StringBuilder draft = new();
    private MicWork? preview;
    private CancellationTokenSource? previewInference;
    private long segmentRevision;
    private long sourceSegmentRevision;
    private int nextPreviewSamples = FirstPreviewSamples;
    private bool previewVisible;

    public bool HasPending { get { lock (gate) return pending.Reader.TryPeek(out _) || preview is not null; } }

    public bool TryRead(out MicWork? work)
    {
        lock (gate)
        {
            if (pending.Reader.TryRead(out work)) return true;
            work = preview;
            preview = null;
            return work is not null;
        }
    }

    public async Task Update(MicCommand? command, CancellationToken ct)
    {
        bool same;
        lock (gate) same = command is not null && command.Id == current?.Id;
        if (!same)
        {
            lock (gate) { recording = false; current = null; InvalidatePreview(); lease?.Cancel(); }
            if (lease is not null) await capture.StopAsync();
            lock (gate) { lease?.Dispose(); lease = null; }
            while (pending.Reader.TryRead(out _)) { }
            lock (gate) if (command is null || command.Id == retired) return;
            if (command.Action == "finish")
            {
                retired = command.Id;
                output(new { micId = command.Id, micState = "final", text = "" });
                return;
            }

            lock (gate)
            {
                current = command;
                draft.Clear();
                dsp.Reset();
                segmenter.Reset();
                segmenter.SetManualGate(true);
                sourceSegmentRevision = segmenter.SegmentRevision;
                recording = true;
                lease = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lease.CancelAfter(1700);
            }
            try
            {
                await capture.StartAsync("mic", null, (samples, _) => Feed(command, samples),
                    _ => Fail(command), lease.Token, command.Device);
                lock (gate)
                    if (recording && current == command) output(new { micId = command.Id, micState = "recording" });
            }
            catch (Exception) when (!ct.IsCancellationRequested) { Fail(command); }
            return;
        }

        lock (gate)
        {
            if (lease?.IsCancellationRequested == true && recording) Fail(command!);
            if (command!.Action != "finish" && recording) lease?.CancelAfter(1700);
            if (command.Action != "finish" || !recording) return;
            recording = false;
            InvalidatePreview();
            Queue(command, segmenter.SetManualGate(false));
            if (current is not null) Enqueue(new(command.Id, command.Language, null));
        }
        await capture.StopAsync();
    }

    private void Feed(MicCommand command, float[] samples)
    {
        lock (gate)
        {
            if (!recording || lease?.IsCancellationRequested != false || command.Id != current?.Id) return;
            var parts = segmenter.Feed(samples, dsp, out _);
            long sourceRevision = segmenter.SegmentRevision;
            if (sourceSegmentRevision != sourceRevision)
            {
                if (parts.Length == 0 && previewVisible)
                    output(new { micId = command.Id, micState = "partial", text = draft.ToString() });
                sourceSegmentRevision = sourceRevision;
                InvalidatePreview();
            }
            foreach (var part in parts) Queue(command, part);
            if (!recording || pending.Reader.TryPeek(out _) || segmenter.SnapshotPreview(nextPreviewSamples) is not { } snapshot) return;
            nextPreviewSamples = snapshot.Length + AudioDspPipeline.OutputSampleRate * previewIntervalMs / 1000;
            // Keep one preview slot. Fresh audio replaces an older snapshot that has not started yet.
            preview = new(command.Id, command.Language, snapshot, segmentRevision);
            wake();
        }
    }

    private void Queue(MicCommand command, CompletedAudioSegment? part)
    {
        if (part is null || current is null) return;
        Enqueue(new(command.Id, command.Language, part.CleanMono16k));
    }

    private void InvalidatePreview()
    {
        segmentRevision++;
        nextPreviewSamples = FirstPreviewSamples;
        preview = null;
        previewVisible = false;
        previewInference?.Cancel();
    }

    private void Enqueue(MicWork work)
    {
        if (pending.Writer.TryWrite(work)) wake();
        else if (current is { } command) Fail(command);
    }

    public async Task Recognize(MicWork work, VoiceRecognition recognizer, CancellationToken ct)
    {
        if (work.PreviewSegment.HasValue)
        {
            await RecognizePreview(work, recognizer, ct);
            return;
        }
        lock (gate) if (work.Id != current?.Id) return;
        var result = work.Samples is null ? null : await recognizer.Read(work.Samples, work.Language, ct);
        lock (gate)
        {
            if (work.Id != current?.Id) return;
            if (result?.Text is { Length: > 0 } phrase)
            {
                if (draft.Length > 0) draft.Append(' ');
                draft.Append(phrase);
            }
            if (work.Samples is not null)
            {
                output(new { micId = work.Id, micState = "partial", text = draft.ToString() });
                return;
            }
            retired = work.Id;
            current = null;
            output(new { micId = work.Id, micState = "final", text = draft.ToString() });
        }
    }

    private async Task RecognizePreview(MicWork work, VoiceRecognition recognizer, CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (gate)
        {
            if (!recording || work.Id != current?.Id || work.PreviewSegment != segmentRevision) return;
            previewInference = cancellation;
        }
        try
        {
            var result = await recognizer.Read(work.Samples!, work.Language, cancellation.Token, preview: true);
            lock (gate)
            {
                if (cancellation.IsCancellationRequested || !recording || work.Id != current?.Id ||
                    work.PreviewSegment != segmentRevision || result.Text.Length == 0) return;
                string text = draft.Length == 0 ? result.Text : draft.ToString() + " " + result.Text;
                previewVisible = true;
                output(new { micId = work.Id, micState = "preview", text });
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { lock (gate) previewInference = null; }
    }

    private void Fail(MicCommand command)
    {
        lock (gate)
        {
            if (current?.Id != command.Id) return;
            retired = command.Id;
            current = null;
            recording = false;
            InvalidatePreview();
            lease?.Cancel();
            output(new { micId = command.Id, micState = "error" });
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Update(null, CancellationToken.None);
        capture.Dispose();
    }
}
