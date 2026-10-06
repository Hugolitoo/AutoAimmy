using Aimmy2.AdaptiveControl;
using Aimmy2.Adaptive;
using System.Text.Json;

int checks = 0;
void Check(bool value, string description)
{
    if (!value) throw new Exception(description);
    checks++;
}
bool Near(double a, double b, double tolerance = 1e-7) => Math.Abs(a - b) < tolerance;
DetectionSample Box(int index, double x = 1000, double y = 540, double width = 70, double height = 140,
    double confidence = .9, int classId = 0) => new(index, x, y, width, height, confidence, classId);

var duplicates = AdaptiveTargetTracker.SuppressDuplicates(new[]
{
    Box(0), Box(1, x: 1001, confidence: .7), Box(2, x: 1000, classId: 1), Box(3, x: 1100),
    Box(4, confidence: .1), Box(5, x: double.NaN), Box(6, height: -1)
});
Check(duplicates.Count == 3 && duplicates.Any(d => d.SourceIndex == 0) && duplicates.All(d => d.SourceIndex != 1),
    "class-specific NMS suppresses duplicates and rejects invalid/low confidence boxes");
Check(Near(AdaptiveTargetTracker.IntersectionOverUnion(Box(0), Box(1)), 1), "IoU uses center coordinates");

var tracker = new AdaptiveTargetTracker();
var first = tracker.Update(0, new[] { Box(0), Box(1, x: 1100) }, 960, 540)!;
var next = tracker.Update(.02, new[] { Box(9, x: 1002), Box(7, x: 1101) }, 960, 540)!;
Check(next.Id == first.Id && next.Detection.SourceIndex == 9 && tracker.VisibleTracks.Count == 2,
    "track identity survives source-index reorder while all targets are tracked");
Check(next.VelocityX > 0 && next.VelocityX < 100, "velocity is smoothed in pixels per second");
Check(tracker.Update(.04, Array.Empty<DetectionSample>(), 960, 540) == null, "missing detection never produces ghost output");
var resumed = tracker.Update(.11, new[] { Box(0, x: 1003) }, 960, 540)!;
Check(resumed.Id == first.Id && resumed.VelocityX == 0 && resumed.ConsecutiveFrames == 1,
    "brief reacquisition retains identity but resets stale velocity and confirmation");
var afterGap = tracker.Update(.4, new[] { Box(0, x: 1003) }, 960, 540)!;
Check(afterGap.Id != first.Id && afterGap.ConsecutiveFrames == 1, "long gaps cannot retain an old identity");
var slowerTracker = new AdaptiveTargetTracker(); TargetTrack? slowerTrack = null;
for (int slowerFrame = 0; slowerFrame < 6; slowerFrame++)
    slowerTrack = slowerTracker.Update(slowerFrame * .1, [Box(0, x: 1000 + slowerFrame)], 960, 540);
Check(slowerTrack?.ConsecutiveFrames == 6, "valid CPU-rate frames confirm a target without treating every 100 ms frame as a reacquisition");

tracker.Reset();
first = tracker.Update(0, new[] { Box(0, x: 1080, width: 30, height: 60) }, 960, 540)!;
tracker.Update(.1, new[] { Box(0, x: 1080, width: 30, height: 60) }, 960, 540);
var competitors = new[] { Box(0, x: 1080, width: 30, height: 60), Box(1, x: 970, width: 30, height: 60) };
Check(tracker.Update(.16, competitors, 960, 540)!.Id == first.Id, "one centered competitor cannot steal the lock");
Check(tracker.Update(.18, competitors, 960, 540)!.Id == first.Id, "two competitor frames retain lock");
Check(tracker.Update(.20, competitors, 960, 540)!.Id != first.Id, "three clearly better frames can switch after dwell");
Check(tracker.Update(.21, new[] { Box(0, x: 5000) }, 960, 540) == null, "out-of-range detections do not generate a target");

CalibrationResult Calibrate(bool noisy = false, bool oneDirection = false, double yGain = -3, double interval = .11)
{
    var session = new GuidedCalibration("held:settings-a", 1080);
    double t = 0, x = 1000, y = 540;
    session.Observe(new(t, 1, x, y, 0, 0, true, TargetHeight: 120));
    for (int axis = 0; axis < 2; axis++)
        for (int i = 0; i < 36; i++)
        {
            t += interval;
            double count = oneDirection || i % 6 < 3 ? 10 : -10;
            double pixels = count * (axis == 0 ? -2 : yGain);
            if (noisy && i % 2 == 0) pixels *= -.7;
            if (axis == 0) x += pixels; else y += pixels;
            session.Observe(new(t, 1, x, y, axis == 0 ? count : 0, axis == 1 ? count : 0, true, TargetHeight: 120));
        }
    return session.Evaluate();
}

var measured = Calibrate();
Check(measured.IsUsable && measured.Success && Near(measured.PixelsPerCountX, -2) && Near(measured.PixelsPerCountY, -3),
    "guided bidirectional stationary-target motion recovers signed pixels per raw count");
Check(measured.FitX > .99 && measured.SamplesX >= 12 && measured.SamplesY >= 12,
    "calibration retains fit and independent axis evidence");
Check(!Calibrate(oneDirection: true).IsUsable, "one-direction motion is insufficient calibration evidence");
Check(!Calibrate(noisy: true).IsUsable, "inconsistent moving target cannot pass the calibration fit");
Check(Calibrate(yGain: 3).IsUsable, "inverted vertical response is calibrated instead of assumed");
Check(Calibrate(interval: .3).IsUsable,
    "matched passive measurements at 300 ms intervals can calibrate without changing live correction bounds");

GuidedCalibration CalibrateSmallMovements(double interval, bool noisy = false, double xGain = -2, double yGain = -3)
{
    var session = new GuidedCalibration("held:settings-a", 1080);
    double time = 0, x = 1000, y = 540;
    var random = new Random(31);
    session.Observe(new(time, 1, x, y, 0, 0, true, TargetHeight: 120));
    for (int axis = 0; axis < 2; axis++)
        for (int sweep = 0; sweep < 8; sweep++)
            for (int step = 0; step < 24; step++)
            {
                double raw = sweep % 2 == 0 ? 1 : -1;
                if (axis == 0) x += raw * xGain; else y += raw * yGain;
                double jitter = noisy ? (random.NextDouble() - .5) * 30 : 0;
                session.Observe(new(time += interval, 1, x + (axis == 0 ? jitter : 0),
                    y + (axis == 1 ? jitter : 0), axis == 0 ? raw : 0, axis == 1 ? raw : 0,
                    true, TargetHeight: 120));
            }
    return session;
}
foreach (double interval in new[] { .05, .10, .12 })
{
    var small = CalibrateSmallMovements(interval);
    var result = small.Evaluate();
    Check(result.IsUsable && Near(result.PixelsPerCountX, -2) && Near(result.PixelsPerCountY, -3),
        $"one-count slow movements accumulate reliable bidirectional evidence at {interval * 1000:0} ms intervals");
    Check(small.Progress.X.CleanSamples >= 12 && small.Progress.Y.CleanSamples >= 12 &&
        small.Progress.X.PositiveSamples >= 3 && small.Progress.X.NegativeSamples >= 3 &&
        small.Progress.Y.PositiveSamples >= 3 && small.Progress.Y.NegativeSamples >= 3 &&
        small.Progress.X.TotalCounts >= 160 && small.Progress.Y.TotalCounts >= 160,
        "small movements retain every original sample, direction and total-movement requirement");
}
Check(!CalibrateSmallMovements(.05, noisy: true).Evaluate().IsUsable,
    "accumulating small movements cannot turn noisy independent target motion into a usable fit");
var subpixelCalibration = CalibrateSmallMovements(.05, xGain: -.4, yGain: .5);
Check(subpixelCalibration.Evaluate().IsUsable && Near(subpixelCalibration.Progress.X.PixelsPerCount, -.4) &&
    Near(subpixelCalibration.Progress.Y.PixelsPerCount, .5),
    "subpixel per-frame movement accumulates a measurable two-pixel signal without weakening fit or spread requirements");
var signalCalibration = new GuidedCalibration("held:settings-a", 1080);
signalCalibration.Observe(new(0, 1, 1000, 540, 0, 0, true));
signalCalibration.Observe(new(.1, 1, 999, 540, 4, 0, true));
Check(signalCalibration.SamplesX == 0, "one-pixel motion is held for more signal even when the raw count minimum is satisfied");
signalCalibration.Observe(new(.2, 1, 998, 540, 4, 0, true));
Check(signalCalibration.Progress.X.Samples == 1 && Near(signalCalibration.Progress.X.TotalCounts, 8) &&
    Near(signalCalibration.Progress.X.PixelsPerCount, -.25), "longer windows retain the matched counts while reaching two pixels of evidence");

var reversalCalibration = new GuidedCalibration("held:settings-a", 1080);
double reversalTime = 0, reversalX = 1000;
reversalCalibration.Observe(new(0, 1, reversalX, 540, 0, 0, true));
foreach (double raw in new[] { 2.0, 1, -1, -1, -1, -1 })
{
    reversalX -= raw * 2;
    reversalCalibration.Observe(new(reversalTime += .05, 1, reversalX, 540, raw, 0, true));
}
Check(reversalCalibration.Progress.X.Samples == 1 && reversalCalibration.Progress.X.NegativeSamples == 1 &&
    Near(reversalCalibration.Progress.X.TotalCounts, 4) && Near(reversalCalibration.Progress.X.PixelsPerCount, -2),
    "a reversal discards an insufficient outgoing segment and measures the incoming direction without cancelled counts");
var jitterCalibration = new GuidedCalibration("held:settings-a", 1080);
double jitterX = 1000;
jitterCalibration.Observe(new(0, 1, jitterX, 540, 0, 0, true));
for (int frame = 1; frame <= 120; frame++)
{
    double raw = frame % 2 == 0 ? 1 : -1;
    jitterX -= raw * 2;
    jitterCalibration.Observe(new(frame * .05, 1, jitterX, 540, raw, 0, true));
}
Check(jitterCalibration.SamplesX == 0 && !jitterCalibration.Evaluate().IsUsable,
    "rapid sub-threshold reversals never merge into apparent movement evidence");

var sparseCalibration = new GuidedCalibration("held:settings-a", 1080);
sparseCalibration.Observe(new(0, 1, 1000, 540, 0, 0, true));
for (int frame = 1; frame <= 20; frame++)
    sparseCalibration.Observe(new(frame * .25, 1, 1000 - frame * 2, 540, 1, 0, true));
Check(sparseCalibration.SamplesX == 0,
    "insufficient movement is never accumulated beyond the bounded calibration window");
var gapCalibration = new GuidedCalibration("held:settings-a", 1080);
gapCalibration.Observe(new(0, 1, 1000, 540, 0, 0, true));
gapCalibration.Observe(new(.1, 1, 998, 540, 1, 0, true));
gapCalibration.Observe(new(.7, 1, 960, 540, 19, 0, true));
Check(gapCalibration.SamplesX == 0, "a gap over 500 ms breaks passive calibration without measuring unseen motion");
gapCalibration.Observe(new(.8, 1, 956, 540, 2, 0, true));
gapCalibration.Observe(new(.9, 1, 952, 540, 2, 0, true));
Check(gapCalibration.Progress.X.Samples == 1 && Near(gapCalibration.Progress.X.TotalCounts, 4) &&
    Near(gapCalibration.Progress.X.PixelsPerCount, -2), "calibration resumes after a long gap with only the new matched displacement");

var progressCalibration = new GuidedCalibration("held:settings-a", 1080);
double progressTime = 0, progressX = 1000;
progressCalibration.Observe(new(0, 1, progressX, 540, 0, 0, true));
for (int sample = 0; sample < 12; sample++)
{
    double raw = sample < 6 ? 4 : -4;
    progressX -= raw * 2;
    progressCalibration.Observe(new(progressTime += .11, 1, progressX, 540, raw, 0, true));
}
Check(progressCalibration.Progress.X.CleanSamples == 12 && progressCalibration.Progress.X.PositiveSamples == 6 &&
    progressCalibration.Progress.X.NegativeSamples == 6 && Near(progressCalibration.Progress.X.TotalCounts, 48) &&
    progressCalibration.Progress.X.MissingReason == "MoreMovementRequired" && !progressCalibration.Progress.X.Complete &&
    progressCalibration.Progress.Y.MissingReason == "SamplesRequired",
    "structured progress explains why twelve clean samples alone do not complete an axis");
var oneDirectionProgress = new GuidedCalibration("held:settings-a", 1080);
oneDirectionProgress.Observe(new(0, 1, 1000, 540, 0, 0, true));
for (int sample = 1; sample <= 20; sample++)
    oneDirectionProgress.Observe(new(sample * .11, 1, 1000 - sample * 20, 540, 10, 0, true));
Check(oneDirectionProgress.Progress.X.MissingReason == "BothDirectionsRequired" &&
    oneDirectionProgress.Progress.X.NegativeSamples == 0 && !oneDirectionProgress.Progress.X.Complete,
    "progress identifies a missing direction even with enough samples and movement");

var sessionOutput = new GuidedCalibration("held:settings-a", 1080);
sessionOutput.Observe(new(0, 1, 1000, 540, 0, 0, true));
sessionOutput.Observe(new(.1, 1, 980, 540, 10, 0, true, true));
Check(sessionOutput.SamplesX == 0 && sessionOutput.Status == "OutputMustBeDisabled", "generated output invalidates passive calibration intervals");
var sessionIdentity = new GuidedCalibration("held:settings-a", 1080);
sessionIdentity.Observe(new(0, 1, 1000, 540, 0, 0, true));
sessionIdentity.Observe(new(.11, 2, 980, 540, 10, 0, true));
Check(sessionIdentity.SamplesX == 0, "identity changes cannot create calibration displacement samples");
var driftCalibration = new GuidedCalibration("held:settings-a", 1080);
double driftTime = 0, driftX = 1000, driftY = 540;
driftCalibration.Observe(new(0, 1, driftX, driftY, 0, 0, true));
for (int axis = 0; axis < 2; axis++)
    for (int i = 0; i < 36; i++)
    {
        double raw = i % 6 < 3 ? 10 : -10;
        double movement = raw * (i < 18 ? -2 : -4);
        if (axis == 0) driftX += movement; else driftY += movement;
        driftCalibration.Observe(new(driftTime += .11, 1, driftX, driftY, axis == 0 ? raw : 0, axis == 1 ? raw : 0, true));
    }
Check(!driftCalibration.Evaluate().IsUsable, "calibration rejects a zoom/gain change halfway through sampling even when origin-fit R squared looks high");

var options = new AdaptiveControlOptions { AimPointHeightFraction = .5 };
var engine = new AdaptiveAimEngine(options);
AdaptiveFrame Frame(double time, bool allowed = true, bool held = true, string key = "held:settings-a", double height = 140,
    double x = 1000, double y = 540, double screenHeight = 1080) =>
    new(time, 960, 540, screenHeight, new[] { Box(0, x, y, height: height) }, held, allowed, key);
for (int i = 0; i < 10; i++)
    Check(!engine.Update(Frame(i / 60.0)).HasCorrection, "uncalibrated frames never emit movement");
engine.SetCalibration(measured);
var initial = engine.Update(Frame(0));
Check(!initial.HasCorrection, "first calibrated detection must stabilize before assistance");
AdaptiveDecision decision = initial;
for (int i = 1; i <= 30; i++) decision = engine.Update(Frame(i / 60.0));
Check(decision.CountsX > 0 && decision.CountsY == 0 && decision.Status == "CalibratedAssistance",
    "negative calibrated camera response generates positive horizontal correction");
Check(!engine.Update(Frame(.52, allowed: false)).HasCorrection, "observation output guard stops correction immediately");
Check(!engine.Update(Frame(.54, held: false)).HasCorrection, "activation release stops correction immediately");
Check(!engine.Update(Frame(.56, key: "released:settings-a")).Calibrated, "calibration cannot silently apply to a different stance key");
Check(!engine.Update(Frame(.58, screenHeight: 1440)).HasCorrection, "resolution change requires calibration");
Check(!engine.Update(Frame(.8)).HasCorrection, "long inference gap clears movement and residuals");
Check(!engine.Update(Frame(double.NaN)).HasCorrection, "non-finite frame timestamp cannot emit input");
Check(!engine.Update(new(1, 960, 540, 1080, Array.Empty<DetectionSample>(), true, true, "held:settings-a")).HasCorrection,
    "empty detections stop movement");

engine.Reset();
double totalCounts = 0;
for (int i = 0; i < 180; i++)
{
    decision = engine.Update(Frame(i / 120.0, x: 1190, y: 600));
    Check(Math.Sqrt(decision.CountsX * decision.CountsX + decision.CountsY * decision.CountsY) <= 24,
        "each output obeys vector count cap");
    totalCounts += Math.Sqrt(decision.CountsX * decision.CountsX + decision.CountsY * decision.CountsY);
}
Check(totalCounts < 600 * 1.5 + 3, "high-refresh output remains rate bounded");
engine.Reset();
for (int i = 0; i < 60; i++) decision = engine.Update(Frame(i / 60.0, allowed: false, height: 60));
Check(decision.ContextKey == "Small/Slow", "small apparent image size automatically selects its subprofile");
for (int i = 60; i < 63; i++) decision = engine.Update(Frame(i / 60.0, allowed: false, height: 270));
Check(decision.ContextKey == "Small/Slow", "brief size changes do not flicker the active subprofile");
for (int i = 63; i < 120; i++) decision = engine.Update(Frame(i / 60.0, allowed: false, height: 270));
Check(decision.ContextKey == "Large/Slow", "persistent new apparent size selects a new subprofile");

engine.Reset();
for (int i = 0; i < 240; i++) engine.Update(Frame(i / 60.0, x: i % 2 == 0 ? 982 : 938));
var adapted = engine.Snapshot();
Check(adapted.Profiles.Length == 9 && adapted.Profiles.Any(p => p.ErrorSignCrossings >= 4),
    "nine local contexts retain observed assisted-error crossings");
Check(adapted.Profiles.Any(p => p.AdjustmentEvidence.StartsWith("RepeatedScreenErrorCrossings")),
    "repeated error crossings reduce gain conservatively with explicit limited evidence");
Check(adapted.Profiles.All(p => p.Gain is >= .06 and <= .22), "automatic adaptation stays inside hard bounds");

foreach (double fps in new[] { 20.0, 30.0, 60.0, 120.0 })
{
    var loop = new AdaptiveAimEngine(options);
    loop.SetCalibration(measured);
    double position = 1100, minimum = position;
    for (int i = 0; i < fps * 5; i++)
    {
        var response = loop.Update(Frame(i / fps, x: position));
        position += response.CountsX * measured.PixelsPerCountX;
        minimum = Math.Min(minimum, position);
    }
    Check(Math.Abs(position - 960) <= 3, $"closed loop converges on stationary target at {fps} FPS");
    Check(minimum >= 958, $"closed loop bounds stationary-target overshoot at {fps} FPS");
}
var mixedLoop = new AdaptiveAimEngine(options);
mixedLoop.SetCalibration(measured);
double mixedTime = 0, mixedPosition = 1030, maximumError = 0;
for (int i = 0; i < 500; i++)
{
    double dt = i % 3 == 0 ? .035 : .012;
    mixedTime += dt;
    // Independent screen motion and imperfect user correction are injected into the plant.
    mixedPosition += 18 * dt + Math.Sin(i * .33) * .7;
    var response = mixedLoop.Update(Frame(mixedTime, x: mixedPosition));
    mixedPosition += response.CountsX * measured.PixelsPerCountX;
    if (i > 120) maximumError = Math.Max(maximumError, Math.Abs(mixedPosition - 960));
}
Check(maximumError < 15, "variable-frame closed loop remains bounded with independent motion and imperfect user correction");

engine.ObserveCameraGain(-4, -6, .5, 40);
Check(engine.Snapshot().Calibration != null, "unreliable gain estimates cannot invalidate calibration");
for (int i = 0; i < 3; i++) engine.ObserveCameraGain(-4, -6, .99, 40);
Check(engine.Snapshot().Calibration == null, "repeated independently measured zoom/gain mismatch suspends calibration");
decision = engine.Update(Frame(4.1));
Check(!decision.HasCorrection && decision.Status == "CameraGainChangedRecalibrate", "suspended calibration cannot generate output");

var temp = Path.Combine(Path.GetTempPath(), "AutoAimmy-control-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    string path = Path.Combine(temp, "profile.json");
    var persisted = adapted with { Calibration = measured, PlayerKey = "player-hash" };
    AdaptiveProfileStore.Save(path, persisted);
    var loaded = AdaptiveProfileStore.Load(path, out var error);
    Check(error == null && loaded.PlayerKey == "player-hash" && loaded.Profiles.Length == 9 && loaded.Calibration!.IsUsable,
        "local atomic checkpoint round-trips measured calibration and all context evidence");
    var restored = new AdaptiveAimEngine(options, loaded);
    Check(Near(restored.Snapshot().Profiles.Sum(p => p.Gain), loaded.Profiles.Sum(p => p.Gain)),
        "engine restores bounded context gains across restart");
    Check(Directory.GetFiles(temp).Length == 1, "successful checkpoint leaves no temporary files");
    File.WriteAllText(path, "{ broken json");
    loaded = AdaptiveProfileStore.Load(path, out error);
    Check(error != null && loaded.Calibration == null, "corrupt profile safely resets with a diagnostic");
    File.WriteAllText(path, new string('x', 65537));
    Check(AdaptiveProfileStore.Load(path, out error).Calibration == null && error != null, "oversize profile is rejected");
    var sanitized = AdaptiveProfileStore.Sanitize(persisted with
    {
        PlayerKey = "C:\\private\\account", Calibration = measured with { PixelsPerCountX = double.NaN },
        Profiles = new[] { new ContextProfile { Key = "Small/Slow", Gain = 50, SmoothingSeconds = double.NaN },
            new ContextProfile { Key = "arbitrary-private-content", Gain = .15 } }
    });
    Check(sanitized.PlayerKey == "local" && sanitized.Calibration == null && sanitized.Profiles.Length == 1 &&
        sanitized.Profiles[0].Gain == .22 && double.IsFinite(sanitized.Profiles[0].SmoothingSeconds),
        "untrusted persisted settings cannot escape finite bounds or known context keys");
}
finally { Directory.Delete(temp, true); }

var sceneRandom = new Random(15);
byte[] scenePrevious = new byte[160 * 120]; sceneRandom.NextBytes(scenePrevious);
byte[] sceneCurrent = new byte[scenePrevious.Length];
for (int sceneY = 3; sceneY < 120; sceneY++) for (int sceneX = 2; sceneX < 160; sceneX++)
    sceneCurrent[sceneY * 160 + sceneX] = scenePrevious[(sceneY - 3) * 160 + sceneX - 2];
for (int sceneY = 40; sceneY < 75; sceneY++) for (int sceneX = 55; sceneX < 90; sceneX++) sceneCurrent[sceneY * 160 + sceneX] = 255;
var registration = SceneMotionEstimator.Estimate(scenePrevious, sceneCurrent, 160, 120, [(55, 40, 35, 35)]);
Check(registration.Reliable && Math.Abs(registration.X - 2) < .2 && Math.Abs(registration.Y - 3) < .2,
    "background consensus recovers camera translation while excluding an independently moving target");
Check(!SceneMotionEstimator.Estimate(new byte[160 * 120], new byte[160 * 120], 160, 120, []).Reliable,
    "flat scenery cannot produce a fictitious camera estimate");
byte[] smoothScene = new byte[160 * 120], fractionalScene = new byte[160 * 120];
for (int sceneY = 1; sceneY < 119; sceneY++) for (int sceneX = 1; sceneX < 159; sceneX++)
{
    int sum = 0;
    for (int yy = -1; yy <= 1; yy++) for (int xx = -1; xx <= 1; xx++) sum += scenePrevious[(sceneY + yy) * 160 + sceneX + xx];
    smoothScene[sceneY * 160 + sceneX] = (byte)(sum / 9);
}
for (int sceneY = 2; sceneY < 118; sceneY++) for (int sceneX = 2; sceneX < 158; sceneX++)
    fractionalScene[sceneY * 160 + sceneX] = (byte)(smoothScene[sceneY * 160 + sceneX] * .75 + smoothScene[sceneY * 160 + sceneX - 1] * .25);
var fractionalRegistration = SceneMotionEstimator.Estimate(smoothScene, fractionalScene, 160, 120, []);
Check(fractionalRegistration.Reliable && Math.Abs(fractionalRegistration.X - .25) < .12 && Math.Abs(fractionalRegistration.Y) < .1,
    "subpixel background registration measures slow camera motion without integer-pixel gain quantization");
var cameraOnlyEngine = new AdaptiveAimEngine();
for (int sceneFrame = 0; sceneFrame < 100; sceneFrame++)
    cameraOnlyEngine.Update(new AdaptiveFrame(sceneFrame * .01, 960, 540, 1080,
        [Box(0, x: 1000 + sceneFrame)], SceneVelocityX: 100, SceneVelocityY: 0, SceneMotionReliable: true));
var discovered = cameraOnlyEngine.Snapshot().Profiles.Where(p => p.Key.StartsWith("Adaptive/")).ToArray();
Check(discovered.Length > 0 && discovered.All(p => p.SpeedFeature < .02),
    "new situational profiles distinguish camera translation from enemy motion");
Check(AdaptiveProfileStore.Sanitize(cameraOnlyEngine.Snapshot()).Profiles.Any(p => p.Key.StartsWith("Adaptive/")),
    "discovered profiles survive bounded local persistence");
var tunerCheck = new ContextTuner(); TuningResult? tuningCheckResult = null;
for (int tuneWindow = 0; tuneWindow < 40; tuneWindow++)
{
    var parameters = tunerCheck.BeginWindow(.15, .07);
    tuningCheckResult = tunerCheck.EndWindow(parameters.Candidate ? .5 : 1, 0);
    if (tuningCheckResult.Completed) break;
}
Check(tuningCheckResult?.Accepted == true && tuningCheckResult.Windows == 32 && tuningCheckResult.Gain is >= .06 and <= .22,
    "configuration changes require 16 matched windows per arm and a measured gain beyond uncertainty");
var identicalTuner = new ContextTuner(); bool identicalAccepted = false;
for (int tuneWindow = 0; tuneWindow < 40; tuneWindow++)
{
    identicalTuner.BeginWindow(.15, .07); var result = identicalTuner.EndWindow(1, 0);
    if (result.Completed) { identicalAccepted = result.Accepted; break; }
}
Check(!identicalAccepted, "no tracking gain means no configuration promotion");

Check(AmmoHudParser.Parse(new("31/150", [])) == (31, 150), "explicit magazine/reserve counter is read together");
Check(AmmoHudParser.Parse(new("100/100 2.5x", [])) == (null, null), "HUD health-like values are not accepted as a magazine");
Check(AmmoHudParser.Parse(new("31/150 20/120", [])) == (null, null), "conflicting ammo counters remain unknown");
Check(AmmoHudParser.Parse(new("31 150", [new("31", 10, 10, 30, 30), new("150", 50, 18, 30, 16)])) == (31, 150),
    "OCR geometry pairs a larger magazine number with the nearby smaller reserve");
Check(AmmoHudParser.Parse(new("31 150", [new("31", 10, 10, 30, 30), new("150", 500, 18, 30, 16)])) == (null, null),
    "unrelated distant HUD numbers are not guessed as ammunition");
var recoilEstimate = new RecoilEstimator();
Check(!recoilEstimate.Observe(4, .012, false, true) && !recoilEstimate.Observe(4, .012, true, false),
    "generated input or unreliable background cannot teach recoil");
Check(!recoilEstimate.Observe(1, .003, true, true) && !recoilEstimate.Observe(4, -.012, true, true) &&
    !recoilEstimate.Observe(4, double.NaN, true, true), "single-shot/noisy, negative, and non-finite residuals cannot teach recoil");
for (int burst = 0; burst < 5; burst++) recoilEstimate.Observe(4, .012, true, true);
Check(!recoilEstimate.Reliable, "recoil requires repeated independent firing windows");
recoilEstimate.Observe(4, .012, true, true);
Check(recoilEstimate.Reliable && Near(recoilEstimate.MeanKick, .003) && recoilEstimate.ObservedShots == 24,
    "six coherent measured camera residuals retain normalized recoil and shot evidence");
var noisyRecoil = new RecoilEstimator();
for (int burst = 0; burst < 12; burst++) noisyRecoil.Observe(4, burst % 2 == 0 ? .012 : .06, true, true);
Check(!noisyRecoil.Reliable, "inconsistent burst residuals never activate recoil feedforward");
var sanitizedRecoil = new RecoilEstimator(new([double.NaN, -1, .003, double.PositiveInfinity], -5));
Check(sanitizedRecoil.Windows == 1 && sanitizedRecoil.ObservedShots == 0 && !sanitizedRecoil.Reliable,
    "persisted recoil evidence rejects invalid numbers and negative shot counts");
string recoilTemp = Path.Combine(Path.GetTempPath(), "AutoAimmy-recoil-check-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    RecoilProfileStore.Save(recoilTemp, recoilEstimate.Snapshot());
    var restoredRecoil = new RecoilEstimator(RecoilProfileStore.Load(recoilTemp));
    Check(restoredRecoil.Reliable && Near(restoredRecoil.MeanKick, .003), "recoil evidence survives atomic per-view persistence");
    File.WriteAllText(recoilTemp, new string('x', 4097));
    Check(RecoilProfileStore.Load(recoilTemp) == null, "oversize recoil checkpoints reset safely");
}
finally { if (File.Exists(recoilTemp)) File.Delete(recoilTemp); }
var recoilEngine = new AdaptiveAimEngine(options); recoilEngine.SetCalibration(measured);
AdaptiveFrame RecoilFrame(double t, double pixels = 120, bool scene = true, bool held = true, bool allowed = true) =>
    Frame(t, allowed: allowed, held: held, x: 960) with { SceneMotionReliable = scene, RecoilPixelsPerSecondY = pixels };
int generatedRecoilCounts = 0;
for (int recoilFrame = 0; recoilFrame < 60; recoilFrame++) generatedRecoilCounts += recoilEngine.Update(RecoilFrame(recoilFrame / 60.0)).CountsY;
Check(generatedRecoilCounts > 0 && generatedRecoilCounts <= 42,
    "positive camera kick generates bounded downward counts using the measured signed camera gain");
Check(!recoilEngine.Update(RecoilFrame(1.01, scene: false)).HasCorrection &&
    !recoilEngine.Update(RecoilFrame(1.03, held: false)).HasCorrection &&
    !recoilEngine.Update(RecoilFrame(1.05, allowed: false)).HasCorrection,
    "recoil stops immediately without reliable scenery, activation, or output authorization");
Check(!recoilEngine.Update(RecoilFrame(1.07, pixels: double.NaN)).HasCorrection &&
    !recoilEngine.Update(RecoilFrame(1.09, pixels: -120)).HasCorrection,
    "invalid recoil feedforward does not become movement");
Check(!recoilEngine.Update(RecoilFrame(1.11) with { Detections = [] }).HasCorrection,
    "recoil never continues through an absent detection");
recoilEngine.Reset(); bool boundedRecoil = true;
for (int recoilFrame = 0; recoilFrame < 60; recoilFrame++)
{
    var response = recoilEngine.Update(RecoilFrame(recoilFrame / 60.0, pixels: 1e9));
    boundedRecoil &= Math.Sqrt(response.CountsX * response.CountsX + response.CountsY * response.CountsY) <= 24;
}
Check(boundedRecoil, "extreme recoil estimates cannot escape the unchanged vector count cap");
Console.WriteLine($"{checks} adaptive control checks passed. Synthetic trajectories verify behavior; real-game quality remains unvalidated.");
