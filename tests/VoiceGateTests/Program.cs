using DeadlockVoiceLab.Audio;
using DeadlockVoiceLab.Asr;
using DeadlockVoiceWorker;

var teammate = SegmenterOptions.TeammateVoice;
var previous = teammate with { QuietThresholdDb = null };

static CompletedAudioSegment? Capture(SegmenterOptions options, double amplitude, int durationMs,
    bool open = true, bool centered = true, bool noise = false)
{
    var gate = new VoiceSegmenter(options);
    var dsp = new AudioDspPipeline();
    var random = new Random(745);
    var block = new float[960 * 2];
    for (int i = 0; i < 20; i++) gate.Feed(block, dsp, out _);
    gate.SetManualGate(open);
    for (int ms = 0; ms < durationMs; ms += 20)
    {
        for (int sample = 0; sample < 960; sample++)
        {
            var time = ms / 1000.0 + sample / 48000.0;
            var tone = (float)(amplitude * (noise ? (random.NextDouble() * 2 - 1) : Math.Sin(2 * Math.PI * 1000 * time)));
            block[2 * sample] = tone;
            block[2 * sample + 1] = centered ? tone : -tone;
        }
        if (gate.Feed(block, dsp, out _).Length != 0) throw new Exception("Unexpected early segment");
    }
    return gate.SetManualGate(false);
}

static void Check(bool passed, string name)
{
    if (!passed) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}

var queue = new CaptionQueue();
var recovery = new CaptionRecovery();
recovery.Append(new float[3200]);
recovery.Begin(1, 1);
recovery.Append(new float[16000]);
recovery.End(1000, audible: true);
recovery.Begin(1, 2);
recovery.Append(new float[16000]);
Check(recovery.Take(1100, long.MaxValue, 370, speaking: true) == null, "live speech keeps the recovery task waiting");
Check(recovery.Take(1100, long.MaxValue, 1350, speaking: false) == null, "slow devices reserve inference for live speech");
Check(recovery.Take(2100, long.MaxValue, 1018, speaking: false) is { Utterance: 1, Samples.Length: 19200, Recovery: true, BudgetMs: 1500 },
    "idle retry uses the entire previous utterance and its preroll");
Check(recovery.Take(2200, long.MaxValue, 370, speaking: false) == null, "an utterance can only retry once");
Check(!recovery.IsInWindow(1, 16000), "the recovery deadline starts at the stop edge");
recovery.Begin(1, 3);
Check(!recovery.Contains(1), "a third utterance releases the oldest audio");
var sampleTiming = new CaptionRecovery();
sampleTiming.Begin(1, 4);
sampleTiming.Append(new float[35_840]); // 2.24 s, like one missed callout in the match.
sampleTiming.End(3000, audible: true);
Check(sampleTiming.Take(16_600, 1, 1018, speaking: false) == null,
    "recovery never starts when its budget no longer fits the stop-time window");
Check(sampleTiming.Take(4000, 1, 1018, speaking: false) is { Samples.Length: 35840 },
    "a completed short callout measured at 1.018 s can be retried while idle");
Check(CaptionConfidence.Trust("撤", .85f, .10f, preview: true), "a clear short retreat can appear immediately");
Check(!CaptionConfidence.Trust("push", .90f, .10f, preview: true), "a clipped positive push cannot invert a negated order");
Check(!CaptionConfidence.Trust("go left", .34f, .25f, preview: false), "unclear words are withheld from subtitles");
Check(CaptionConfidence.Trust("Do not push", .85f, .09f, preview: false), "clear negations are preserved for translation");
var draftSchedule = new DraftSchedule();
draftSchedule.Observe(1600);
draftSchedule.Observe(350);
draftSchedule.Observe(1550);
Check(!draftSchedule.Paused, "one slow draft does not disable faster drafts");
draftSchedule.Observe(1700);
draftSchedule.BeginUtterance(queueEmpty: false);
Check(draftSchedule.Paused, "two consecutive overruns pause drafts while final work is queued");
draftSchedule.BeginUtterance(queueEmpty: true);
Check(!draftSchedule.Paused, "the next utterance can probe again after the queue drains");
var first = new CaptionWork(1, 5, new float[48000], 3000, 3, 3000);
var tail = new CaptionWork(1, 6, new float[8000], 3500, 3, 500);
queue.Enqueue(first, out _);
queue.Enqueue(tail, out bool merged);
Check(merged && queue.Take() is { Part: 6, DurationMs: 3500, Samples.Length: 56000 } && queue.IsEmpty,
    "queued adjacent pieces preserve the short tail in one recognition");
queue.Enqueue(first, out _);
queue.Enqueue(tail with { Utterance = 4 }, out merged);
Check(!merged && queue.Enqueue(tail with { Part = 7, Utterance = 5 }, out _) == first &&
    queue.Take()?.Utterance == 4, "overflow retires the oldest interval without combining different intervals");
queue.Clear();
Check(queue.IsEmpty, "a lost signal clears queued audio");
Check(HallucinationFilter.ShouldDropSegment(new string('\u0dc0', 40), .99f, .01f),
    "repeated letters without spaces are rejected even at high decoder confidence");
Check(!HallucinationFilter.ShouldDropSegment("11111111", .99f, .01f),
    "the repeated-letter check does not discard numbers");

const double quiet = 0.0035;
Check(Capture(previous, quiet, 600) == null, "fixture is below the previous floor");
Check(Capture(teammate, quiet, 600) != null, "sustained quiet centered sound survives while teammate is speaking");
Check(Capture(teammate, quiet, 100) == null, "a short quiet burst is discarded");
Check(Capture(teammate, 0, 600) == null, "silence is discarded with the gate open");
Check(Capture(teammate, quiet, 600, centered: false) == null, "off-center noise is discarded");
Check(Capture(teammate, 0.006, 1200, noise: true) == null, "the recovery path rejects centered quiet broadband noise");
Check(Capture(teammate, 0.3, 600, open: false) == null, "loud audio alone cannot open the HUD gate");
var baseline = Capture(previous, 0.1, 160);
var current = Capture(teammate, 0.1, 160);
Check(baseline != null && current != null && current.CleanMono16k.SequenceEqual(baseline.CleanMono16k),
    "previously accepted short sounds retain the same audio");

static float[] Tone(double amplitude, int durationMs, int rate = 48000)
{
    double phase = 2 * Math.PI * 201 / rate;
    return Enumerable.Range(0, rate * durationMs / 1000)
        .Select(i => (float)(amplitude * Math.Sin(phase * i))).ToArray();
}

static List<CompletedAudioSegment> Push(VoiceSegmenter gate, AudioDspPipeline dsp, float[] samples, bool allowSplit = true)
{
    var clips = new List<CompletedAudioSegment>();
    for (int i = 0; i < samples.Length; i += 960)
    {
        var frame = new float[1920];
        for (int j = 0; j < Math.Min(960, samples.Length - i); j++)
            frame[j * 2] = frame[j * 2 + 1] = samples[i + j];
        clips.AddRange(gate.Feed(frame, dsp, out _, allowEarlyManualSplit: allowSplit));
    }
    return clips;
}

var segmenter = new VoiceSegmenter(teammate);
var cleaner = new AudioDspPipeline();
var previewSegmenter = new VoiceSegmenter(teammate, false);
var finalOnlySegmenter = new VoiceSegmenter(teammate, false);
var previewCleaner = new AudioDspPipeline();
var finalOnlyCleaner = new AudioDspPipeline();
previewSegmenter.SetManualGate(true);
finalOnlySegmenter.SetManualGate(true);
Check(Push(previewSegmenter, previewCleaner, Tone(.1, 680)).Count == 0 &&
    previewSegmenter.SnapshotPreview(9600) is { Length: >= 9600 }, "preview captures continuous speech before the first pause");
Push(finalOnlySegmenter, finalOnlyCleaner, Tone(.1, 680));
Push(previewSegmenter, previewCleaner, Tone(.07, 360));
Push(finalOnlySegmenter, finalOnlyCleaner, Tone(.07, 360));
Check(previewSegmenter.SetManualGate(false)!.CleanMono16k.SequenceEqual(finalOnlySegmenter.SetManualGate(false)!.CleanMono16k),
    "taking a preview preserves identical audio for full recognition and the tail");
previewSegmenter.Reset();
previewSegmenter.SetManualGate(true);
Push(previewSegmenter, new AudioDspPipeline(), new float[48000]);
Check(previewSegmenter.SnapshotPreview(9600) is null, "an open silent mic has no preview");
var teammatePreview = new VoiceSegmenter(teammate);
teammatePreview.SetManualGate(true);
long teammatePart = teammatePreview.SegmentRevision;
Check(Push(teammatePreview, new AudioDspPipeline(), Tone(.1, 740)).Count == 0 &&
    teammatePreview.SnapshotPreview(11200) is { Length: >= 11200 },
    "live teammate preview is available while the gate stays open and before the first cut");
Check(teammatePreview.SetManualGate(false)?.Revision == teammatePart && teammatePreview.SegmentRevision != teammatePart,
    "final teammate audio keeps the preview's part identity and invalidates its snapshots");
Push(segmenter, cleaner, new float[19200]);
segmenter.SetManualGate(true);
Check(Push(segmenter, cleaner, Tone(.1, 460)).Count == 0, "continuous speech remains available until a pause");
Check(Push(segmenter, cleaner, new float[12000]).Count == 0, "a short pause does not cut the sentence");
Check(Push(segmenter, cleaner, Tone(quiet, 360)).Count == 0, "quiet voiced ending does not count as a pause");
Check(Push(segmenter, cleaner, new float[24000]).Count == 1, "natural pause emits once within 500 ms while mic stays held");
Check(Push(segmenter, cleaner, new float[48000]).Count == 0, "idle open mic does not repeat the prior phrase");
Check(Push(segmenter, cleaner, Tone(.1, 440)).Count == 0 && segmenter.SetManualGate(false) != null,
    "next utterance is rearmed, and mic release flushes its tail");
Check(segmenter.SetManualGate(false) == null, "duplicate release has no pending audio");

segmenter.Reset();
Push(segmenter, cleaner, new float[19200]);
segmenter.SetManualGate(true);
Push(segmenter, cleaner, Tone(.1, 440), allowSplit: false);
Check(Push(segmenter, cleaner, new float[24000], allowSplit: false).Count == 0
    && segmenter.SetManualGate(false) != null, "backpressure disables early split but mic release still works");

segmenter.Reset();
cleaner.Reset();
Push(segmenter, cleaner, new float[19200]);
segmenter.SetManualGate(true);
Check(Push(segmenter, cleaner, Tone(.1, 3100), allowSplit: false).Count == 1,
    "three-second cap submits during continuous teammate speech under backpressure");
segmenter.Reset();
cleaner.Reset();
Check(segmenter.SetManualGate(false) == null && Push(segmenter, cleaner, new float[19200]).Count == 0,
    "session reset drops the old utterance's unsubmitted audio");

var rng = new Random(17);
var noise = Enumerable.Range(0, 16000).Select(i => (float)(rng.NextDouble() * 2 - 1) *
    (i % 3200 < 2640 ? .1f : 0)).ToArray();
Check(VoiceAudioEvidence.IsEffectLike(noise) && !VoiceAudioEvidence.IsEffectLike(Tone(.02, 500, 16000)),
    "steady broadband bursts and voiced audio carry different acoustic evidence");
Check(HallucinationFilter.ShouldDropSegment("Unexpected subtitle", .36f, .01f, effectLike: true)
    && !HallucinationFilter.ShouldDropSegment("Unexpected subtitle", .36f, .01f),
    "low confidence and effect-like evidence must agree");
Check(HallucinationFilter.ShouldDropSegment("Unexpected subtitle", .85f, .55f, effectLike: true),
    "high no-speech probability suppresses an effect-like clip");
foreach (var callout in new[] { "Back!", "Help", "撤！", "退", "Назад!", "Помогите" })
    Check(!HallucinationFilter.ShouldDropSegment(callout, .51f, .03f, effectLike: true), "short callout survives: " + callout);
