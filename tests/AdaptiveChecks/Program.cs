using Aimmy2.Adaptive;
using System.Text.Json;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}
bool Near(double? actual, double expected) => actual.HasValue && Math.Abs(actual.Value - expected) < 1e-6;
GameplayEvent Sample(double time, double x, bool click = false, long id = 1, long sequence = 0) =>
    new(time, x, 0, 0, 0, click, new TargetObservation(id, 10, 0, 2, 2, .99, 0, time), sequence);

var trajectory = new[] { Sample(0, 0), Sample(.2, 2), Sample(.4, 10), Sample(.5, 13), Sample(.6, 10), Sample(.7, 10, true) };
var f = new FeatureExtractor().Extract(new(1, trajectory, "Click", false));
Check(Near(f.Metrics["ReactionTimeMs"], 200), "reaction onset");
Check(Near(f.Metrics["AcquisitionTimeMs"], 400), "acquisition");
Check(Near(f.Metrics["ClickDelayMs"], 300), "click delay");
Check(Near(f.Metrics["PathLengthPx"], 16), "path length");
Check(Near(f.Metrics["PathEfficiency"], 10.0 / 16), "path efficiency");
Check(Near(f.Metrics["OvershootDistancePx"], 2), "overshoot past far edge");
Check(Near(f.Metrics["CorrectionCount"], 1), "correction hysteresis");
Check(Near(f.Metrics["CorrectionAmplitudePx"], 3), "correction amplitude");
var stationary = new FeatureExtractor().Extract(new(1, new[] { Sample(0, 0), Sample(.1, 0) }, "TargetLost", false));
Check(stationary.Metrics["ReactionTimeMs"] == null && stationary.Metrics["AcquisitionTimeMs"] == null, "missing reaction and acquisition");
Check(stationary.Metrics["PathEfficiency"] == null, "stationary path");
var d = MetricDistribution.From(new double[] { 1, 2, 3, 4, 5, double.NaN })!;
Check(d.Count == 5 && Near(d.Median, 3) && Near(d.P10, 1.4) && Near(d.P90, 4.6), "interpolated quantiles");

var segmenter = new EngagementSegmenter();
var encounters = new List<Engagement>();
segmenter.Completed += encounters.Add;
segmenter.Push(Sample(0, 0));
segmenter.Push(Sample(.05, 2, true));
segmenter.Push(Sample(.06, 3, true));
segmenter.Push(Sample(.07, 4));
segmenter.Push(Sample(.08, 5, id: 2));
segmenter.Push(new(.3, 5, 0, 0, 0, false, null));
Check(encounters.Count == 3, "click hold, target switch, sampling gap boundaries");
Check(encounters[0].EndReason == "Click" && encounters[1].EndReason == "TargetSwitch" && encounters[2].Truncated, "end reasons");
var lost = new EngagementSegmenter();
Engagement? lostEncounter = null;
lost.Completed += e => lostEncounter = e;
lost.Push(Sample(0, 0));
lost.Push(new(.08, 0, 0, 0, 0, false, null));
lost.Push(new(.16, 0, 0, 0, 0, false, null));
Check(lostEncounter?.EndReason == "TargetLost", "brief detection loss grace");
var gap = new EngagementSegmenter();
Engagement? gapEncounter = null;
gap.Completed += e => gapEncounter = e;
gap.Push(Sample(0, 0, sequence: 1));
gap.Push(Sample(.01, 0, sequence: 3));
Check(gapEncounter?.EndReason == "SamplingGap" && gapEncounter.Truncated, "queue drop invalidates encounter");
var analyzer = new SessionAnalyzer();
analyzer.Add(f);
analyzer.Add(f with { Truncated = true });
var profile = analyzer.Analyze();
Check(profile.Engagements == 2 && profile.TruncatedEngagements == 1 && profile.Metrics["ReactionTimeMs"]?.Count == 1, "exclude censored data");

string temp = Path.Combine(Path.GetTempPath(), "AutoAimmy-checks-" + Guid.NewGuid().ToString("N"));
await using (var recorder = new GameplayRecorder(temp, 128))
{
    foreach (var sample in trajectory) Check(recorder.TryRecord(sample with { Timestamp = sample.Timestamp / 10 }), "enqueue observation");
}
var export = JsonSerializer.Deserialize<PlayerStyleProfile>(await File.ReadAllTextAsync(Path.Combine(temp, "analysis.json")))!;
Check(export.Engagements == 1 && Near(export.Metrics["ReactionTimeMs"]?.Median, 20), "roundtrip report");
Check(File.ReadLines(Path.Combine(temp, "events.jsonl")).Count() == trajectory.Length, "drain events on stop");
Check(File.Exists(Path.Combine(temp, "quality.json")), "quality export");

string saturatedDirectory = Path.Combine(temp, "saturated");
long dropped;
await using (var recorder = new GameplayRecorder(saturatedDirectory, 128))
{
    for (int i = 1; i <= 25000; i++) recorder.TryRecord(Sample(i * .001, 0, sequence: i));
    dropped = recorder.DroppedEvents;
}
long written = File.ReadLines(Path.Combine(saturatedDirectory, "events.jsonl")).LongCount();
Check(dropped > 0 && written + dropped == 25000, "bounded overflow accounting and drain");
Check(File.Exists(Path.Combine(saturatedDirectory, "analysis.txt")), "report despite overflow");
Console.WriteLine($"PASS: {checks} checks. Export inspected at {temp}");
