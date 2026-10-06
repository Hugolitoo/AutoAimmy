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
    new(time, x, 0, 0, 0, click, new TargetObservation(id, 10, 0, 2, 2, .99, 0, time), sequence, MotionBaselineValid: true);

var trajectory = new[] { Sample(0, 0), Sample(.2, 2), Sample(.4, 10), Sample(.5, 13), Sample(.6, 10), Sample(.7, 10, true) };
var f = new FeatureExtractor().Extract(new(1, trajectory, "Click", false));
Check(Near(f.Metrics["MovementOnsetLatencyMs"], 200), "reaction onset");
Check(Near(f.Metrics["AcquisitionTimeMs"], 400), "acquisition");
Check(Near(f.Metrics["ClickDelayMs"], 300), "click delay");
Check(Near(f.Metrics["PathLengthPx"], 16), "path length");
Check(Near(f.Metrics["PathEfficiency"], 10.0 / 16), "path efficiency");
Check(Near(f.Metrics["OvershootDistancePx"], 2), "overshoot past far edge");
Check(Near(f.Metrics["CorrectionCount"], 1), "correction hysteresis");
Check(Near(f.Metrics["CorrectionAmplitudePx"], 3), "correction amplitude");
var stationary = new FeatureExtractor().Extract(new(1, new[] { Sample(0, 0), Sample(.1, 0) }, "TargetLost", false));
Check(f.Metrics["ReactionTimeMs"] == null, "cognitive reaction cannot be inferred from detections");
var alreadyMoving = new FeatureExtractor().Extract(new(1, trajectory.Select(a => a with { MotionBaselineValid = false }).ToArray(), "Click", false));
Check(alreadyMoving.Metrics["MovementOnsetLatencyMs"] == null, "already moving has no reaction latency");
GameplayEvent Center(double time, double targetX, double rawDx = 0, bool click = false) =>
    new(time, 1900, 900, 0, 0, click, new(1, targetX, 540, 20, 20, .99, 0, time),
        AimReference: AimReference.ScreenCenter, AimX: 960, AimY: 540, RawMouseDeltaX: rawDx, RawMouseDeltaY: 0, MotionBaselineValid: true);
var centered = new FeatureExtractor().Extract(new(1, new[] { Center(0, 1000), Center(.05, 980, 12), Center(.1, 960, 8), Center(.15, 960, 0, true) }, "Click", false));
var capture = Aimmy2.AILogic.CaptureTargetSelector.SelectDetectionBox("Closest to Center Screen", 640, new System.Drawing.Rectangle(-1920, 0, 1920, 1080), new System.Drawing.Point(-10, 900), true);
Check(capture.X == -1280 && capture.Y == 220, "centered capture ignores cursor and retains monitor origin");
Check(Near(centered.Metrics["AcquisitionTimeMs"], 100), "reticle acquires without desktop cursor movement");
Check(Near(centered.Metrics["TrackingErrorPx"], 20.0 / 3), "tracking references reticle rather than far away cursor");
Check(Near(centered.Metrics["ClickDelayMs"], 50), "reticle click delay");
Check(Near(centered.Metrics["RawMousePathCounts"], 20) && Near(centered.Metrics["RawMousePeakCountsPerSecond"], 240), "raw counts retain physical units");
Check(centered.Metrics["PathLengthPx"] == null && centered.Metrics["PeakVelocityPxPerSecond"] == null && centered.Metrics["OvershootDistancePx"] == null, "reticle does not invent cursor trajectory metrics");
var initialOverlap = new FeatureExtractor().Extract(new(1, new[] { Center(0, 960), Center(.05, 960, 8, true) }, "Click", false));
Check(initialOverlap.Metrics["AcquisitionTimeMs"] == null && initialOverlap.Metrics["ClickDelayMs"] == null && Near(initialOverlap.Metrics["InitiallyOnTarget"], 1), "initial overlap is not a zero acquisition");
var noRaw = new FeatureExtractor().Extract(new(1, new[] { Center(0, 1000), Center(.05, 960) }.Select(a => a with { RawMouseDeltaX = null, RawMouseDeltaY = null, MotionBaselineValid = false }).ToArray(), "Click", false));
Check(noRaw.Metrics["MovementOnsetLatencyMs"] == null && noRaw.Metrics["RawMousePathCounts"] == null, "missing raw telemetry stays unavailable");
Check(stationary.Metrics["MovementOnsetLatencyMs"] == null && stationary.Metrics["AcquisitionTimeMs"] == null, "missing reaction and acquisition");
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
Check(profile.Engagements == 2 && profile.TruncatedEngagements == 1 && profile.Metrics["MovementOnsetLatencyMs"]?.Count == 1, "exclude censored data");

string temp = Path.Combine(Path.GetTempPath(), "AutoAimmy-checks-" + Guid.NewGuid().ToString("N"));
await using (var recorder = new GameplayRecorder(temp, 128))
{
    foreach (var sample in trajectory) Check(recorder.TryRecord(sample with { Timestamp = sample.Timestamp / 10 }), "enqueue observation");
}
var export = JsonSerializer.Deserialize<PlayerStyleProfile>(await File.ReadAllTextAsync(Path.Combine(temp, "analysis.json")))!;
Check(export.Engagements == 1 && Near(export.Metrics["MovementOnsetLatencyMs"]?.Median, 20), "roundtrip report");
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

string settingsDirectory = Path.Combine(temp, "settings");
Directory.CreateDirectory(settingsDirectory);
string settingsPath = Path.Combine(settingsDirectory, "active-profile.json");
Check(PlayerSessionContext.Load(settingsDirectory).Status == "NotConfigured", "missing profile remains unknown");
await File.WriteAllTextAsync(settingsPath, "{\"Schema\":1,\"Label\":\"Test profile\",\"Weapon\":\"M4\",\"Dpi\":1600,\"Scope\":\"2.5x\",\"AdsSensitivity\":55,\"AdsUsageDeclared\":\"melange\",\"Secret\":\"not exported\"}");
var snapshot = PlayerSessionContext.Load(settingsDirectory);
Check(snapshot.Settings?.Dpi == 1600 && snapshot.Settings?.AdsSensitivity == 55 && snapshot.ObservedAdsState == "Unknown", "declared settings never imply observed ADS");
await File.WriteAllTextAsync(settingsPath, "{\"Schema\":1,\"Label\":\"Second profile\",\"Dpi\":800}");
Check(snapshot.Settings?.Dpi == 1600 && PlayerSessionContext.Load(settingsDirectory).Settings?.Dpi == 800, "recording snapshot survives active profile changes");
string contextualSession = Path.Combine(temp, "contextual");
await using (var recorder = new GameplayRecorder(contextualSession, sessionContext: snapshot))
    foreach (var sample in trajectory) recorder.TryRecord(sample);
var contextJson = await File.ReadAllTextAsync(Path.Combine(contextualSession, "context.json"));
Check(contextJson.Contains("1600") && !contextJson.Contains("Secret") && !contextJson.Contains("not exported"), "context whitelist excludes undeclared private fields");
await File.WriteAllTextAsync(settingsPath, "{\"Schema\":1,\"Dpi\":-1}");
Check(PlayerSessionContext.Load(settingsDirectory).Status == "InvalidProfile", "invalid profile is not trusted");
await File.WriteAllTextAsync(settingsPath, "broken JSON");
Check(PlayerSessionContext.Load(settingsDirectory).Status == "InvalidProfile", "malformed profile reported as invalid");
Console.WriteLine($"PASS: {checks} total checks including session context.");

string automaticExports = Path.Combine(temp, "exports");
var automaticRecorder = new GameplayRecorder(Path.Combine(temp, "automatic"), sessionContext: snapshot, reportExportDirectory: automaticExports);
foreach (var sample in trajectory) automaticRecorder.TryRecord(sample);
await automaticRecorder.DisposeAsync();
Check(automaticRecorder.ExportPath != null && automaticRecorder.ExportError == null, "automatic report created after recorder drains");
using (var zip = System.IO.Compression.ZipFile.OpenRead(automaticRecorder.ExportPath!))
{
    Check(zip.Entries.Count == 6 && zip.Entries.All(e => e.Name != "events.jsonl"), "automatic export contains only report whitelist and context");
    using var reader = new StreamReader(zip.GetEntry("engagements.jsonl")!.Open());
    Check((await reader.ReadToEndAsync()) == await File.ReadAllTextAsync(Path.Combine(temp, "automatic", "engagements.jsonl")), "automatic report contains fully flushed engagements");
}
string blockedExports = Path.Combine(temp, "blocked-export");
await File.WriteAllTextAsync(blockedExports, "a file blocks directory creation");
var failedExport = new GameplayRecorder(Path.Combine(temp, "failed-export"), reportExportDirectory: blockedExports);
await failedExport.DisposeAsync();
Check(failedExport.ExportError != null && File.Exists(Path.Combine(temp, "failed-export", "analysis.json")), "export failure preserves recorded session");
Console.WriteLine($"PASS: {checks} total checks including automatic local export.");

await File.WriteAllTextAsync(settingsPath, "{\"Schema\":1,\"Source\":\"SettingsFile\",\"ImportStatus\":\"Imported\",\"HorizontalSensitivity\":3,\"AdsSensitivityByScope\":{\"ADSMouseSensitivity2xHalf\":55,\"PrivateKey\":10},\"AccountPath\":\"private account\"}");
var automaticContext = PlayerSessionContext.Load(settingsDirectory);
Check(automaticContext.SettingsSource == "SettingsFile" && automaticContext.Settings?.ImportStatus == "Imported" && automaticContext.ObservedAdsState == "Unknown", "file settings retain distinct source and do not imply observed ADS");
var automaticJson = JsonSerializer.Serialize(automaticContext);
Check(automaticJson.Contains("ADSMouseSensitivity2xHalf") && !automaticJson.Contains("PrivateKey") && !automaticJson.Contains("AccountPath"), "automatic settings export whitelists scope keys and excludes account paths");
Check(automaticContext.Settings?.Dpi == null && automaticContext.Settings?.Scope == null && automaticContext.Settings?.Weapon == null, "automatic context leaves hardware and equipped items unknown");
Console.WriteLine($"PASS: {checks} total checks including automatic settings provenance.");

Check(HudTextParser.Parse("M4 2.5x") == ("M4", "2.5x"), "HUD explicit weapon and scope text");
Check(HudTextParser.Parse("M416 1920x1080 ammo 25") == (null, null), "HUD substring and resolution are not weapon/scope detections");
Check(HudTextParser.Parse("M4 MP5 1.0x 2.5x") == (null, null), "ambiguous HUD labels stay unknown");
var visualRead = new VisualHudObservation(DateTime.UtcNow, "ReadingHUD", "M4", "2.5x");
string visualSession = Path.Combine(temp, "visual-session");
await using (var visualRecorder = new GameplayRecorder(visualSession))
{
    visualRecorder.TryRecord(Sample(0, 0) with { Hud=visualRead });
    visualRecorder.TryRecord(Sample(.01, 1) with { Hud=visualRead });
    visualRecorder.TryRecord(Sample(.02, 2) with { Hud=visualRead with {ReadUtc=visualRead.ReadUtc.AddSeconds(1), WeaponText=null} });
}
Check(File.ReadAllLines(Path.Combine(visualSession, "visual-events.jsonl")).Length == 2, "visual timeline deduplicates mouse-rate samples and records unknown transitions");
Check(visualRead.AdsState == "Unknown", "OCR labels never imply confirmed ADS");
Console.WriteLine($"PASS: {checks} total checks including visual HUD timeline.");

Check(AmmoHudParser.Parse(new("31 / 120", Array.Empty<HudWord>())) == (31, 120), "explicit ammunition readout");
Check(AmmoHudParser.Parse(new("100 / 100", Array.Empty<HudWord>())) == (null, null), "health-like pair is not ammunition");
Check(AmmoHudParser.Parse(new("", new[] { new HudWord("30", 10, 10, 30, 40), new HudWord("120", 46, 25, 32, 18) })) == (30, 120), "ammunition layout accepts a larger magazine count beside reserve");
Check(AmmoHudParser.Parse(new("", new[] { new HudWord("30", 10, 10, 30, 20), new HudWord("120", 46, 10, 32, 20) })) == (null, null), "ambiguous same-size numbers stay unknown");
byte[] MarkerImage(string shape)
{
    byte[] pixels = new byte[64 * 64 * 3];
    for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
    {
        double dx = x - 31.5, dy = y - 31.5;
        bool light = shape == "full" || shape == "X" && Math.Abs(Math.Abs(dx) - Math.Abs(dy)) <= 2 ||
            shape == "+" && (Math.Abs(dx) <= 2 || Math.Abs(dy) <= 2) ||
            shape == "half" && dx > 0 && Math.Abs(Math.Abs(dx) - Math.Abs(dy)) <= 2;
        if (light) for (int c = 0; c < 3; c++) pixels[(y * 64 + x) * 3 + c] = 255;
    }
    return pixels;
}
double xStrength = ImpactCueDetector.MarkerStrength(MarkerImage("X"), 64);
Check(xStrength > .5, "four diagonal arms have marker contrast");
Check(ImpactCueDetector.MarkerStrength(MarkerImage("+"), 64) < .05 &&
    ImpactCueDetector.MarkerStrength(MarkerImage("full"), 64) < .05 &&
    ImpactCueDetector.MarkerStrength(MarkerImage("half"), 64) < .05, "plus reticle, bright flash and incomplete arms rejected");
var cueDetector = new ImpactCueDetector();
for (int i = 0; i < 8; i++) Check(!cueDetector.Observe(i * .1, xStrength, .1, .02, true, true).Any, "static optic and red clothing do not produce temporal hits");
cueDetector.Reset();
for (int i = 0; i < 4; i++) cueDetector.Observe(i * .1, 0, .01, .01, true, true);
Check(cueDetector.Observe(.4, xStrength, .01, .01, true, true).ProbableHeadMarker, "center marker flash during tracked firing becomes a probable cue");
Check(!cueDetector.Observe(.5, xStrength, .2, .01, true, true).Any, "cue cooldown prevents duplicate hits");
cueDetector.Reset();
for (int i = 0; i < 4; i++) cueDetector.Observe(i * .1, 0, .01, .01, false, true);
Check(!cueDetector.Observe(.4, xStrength, .2, .01, false, true).Any, "no firing means no impact cue");
cueDetector.Reset();
for (int i = 0; i < 4; i++) cueDetector.Observe(i * .1, 0, .01, .01, true, true);
Check(!cueDetector.Observe(.4, 0, .2, .2, true, true).ProbableBlood, "global damage-red flash does not imply target blood");
Check(cueDetector.Observe(.5, 0, .3, .2, true, true).ProbableBlood, "isolated rising target red during firing becomes probable blood");
Check(!cueDetector.Observe(2, xStrength, .5, .2, true, true).Any, "stale image interval resets evidence");
Check(!cueDetector.Observe(2.1, double.NaN, 0, 0, true, true).Any, "invalid visual values rejected");
Console.WriteLine($"PASS: {checks} total checks including ammunition and unverified impact cues.");

