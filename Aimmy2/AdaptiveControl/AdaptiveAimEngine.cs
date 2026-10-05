namespace Aimmy2.AdaptiveControl;

/// <summary>
/// Deterministic, bounded relative-movement controller. It never generates input itself.
/// Profiles summarize screen-space evidence; no metric claims a hit, true distance or recoil correction.
/// </summary>
public sealed class AdaptiveAimEngine
{
    private readonly AdaptiveControlOptions options;
    private readonly AdaptiveTargetTracker tracker;
    private readonly Dictionary<string, ContextProfile> profiles = new(StringComparer.Ordinal);
    private CalibrationResult? calibration;
    private double lastTime = double.NaN, filteredRateX, filteredRateY, residualX, residualY;
    private double candidateSince, gain = .16, smoothing = .07;
    private string context = "Medium/Slow", candidateContext = "Medium/Slow";
    private long lastTrackId;
    private double previousErrorX, previousErrorY;
    private bool previousAssisted;
    private int gainMismatchCount;
    private bool calibrationSuspended;
    private readonly Dictionary<string, int> crossingWindows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> assistedWindows = new(StringComparer.Ordinal);

    public AdaptiveAimEngine(AdaptiveControlOptions? options = null, AdaptivePlayerState? state = null)
    {
        this.options = options ?? new();
        this.options.Validate();
        tracker = new(this.options);
        foreach (var size in Enum.GetValues<ApparentSize>())
            foreach (var motion in Enum.GetValues<ScreenMotion>())
            {
                string key = $"{size}/{motion}";
                profiles[key] = CreateDefault(size, motion);
            }
        if (state != null)
        {
            var valid = AdaptiveProfileStore.Sanitize(state);
            if (valid.Calibration?.IsUsable == true) calibration = valid.Calibration;
            foreach (var profile in valid.Profiles) profiles[profile.Key] = profile;
        }
    }

    public CalibrationResult? Calibration => calibration;
    public IReadOnlyList<TargetTrack> VisibleTracks => tracker.VisibleTracks;

    public void SetCalibration(CalibrationResult result)
    {
        if (!result.IsUsable) throw new ArgumentException("A reliable bidirectional calibration is required.", nameof(result));
        calibration = result;
        calibrationSuspended = false;
        gainMismatchCount = 0;
        Reset();
    }

    /// <summary>Only supply an independent, reliable stationary-reference estimate, never arbitrary target motion.</summary>
    public void ObserveCameraGain(double pixelsPerCountX, double pixelsPerCountY, double fit, int sampleCount)
    {
        if (calibration == null || sampleCount < 12 || !double.IsFinite(fit) || fit < .88 ||
            !CalibrationResult.ValidGain(pixelsPerCountX) || !CalibrationResult.ValidGain(pixelsPerCountY)) return;
        bool mismatch = Math.Sign(pixelsPerCountX) != Math.Sign(calibration.PixelsPerCountX) ||
            Math.Sign(pixelsPerCountY) != Math.Sign(calibration.PixelsPerCountY) ||
            Math.Abs(pixelsPerCountX / calibration.PixelsPerCountX - 1) > .4 ||
            Math.Abs(pixelsPerCountY / calibration.PixelsPerCountY - 1) > .4;
        gainMismatchCount = mismatch ? gainMismatchCount + 1 : 0;
        if (gainMismatchCount >= 3) { calibrationSuspended = true; ClearMovement(); }
    }

    public AdaptiveDecision Update(AdaptiveFrame frame)
    {
        if (!double.IsFinite(frame.TimeSeconds) || !double.IsFinite(frame.CenterX) || !double.IsFinite(frame.CenterY) ||
            !double.IsFinite(frame.ScreenHeight) || frame.ScreenHeight is < 200 or > 10000 || frame.Detections == null)
        { Reset(); return Decision(null, 0, 0, "InvalidFrame", false); }

        double dt = double.IsNaN(lastTime) ? 0 : frame.TimeSeconds - lastTime;
        bool discontinuity = dt <= 0 || dt > options.MaximumFrameGapSeconds;
        if (discontinuity) ClearMovement();
        lastTime = frame.TimeSeconds;
        var target = tracker.Update(frame.TimeSeconds, frame.Detections, frame.CenterX, frame.CenterY);
        bool calibrated = calibration?.IsUsable == true && !calibrationSuspended &&
            calibration.ContextKey == frame.CalibrationContextKey && Math.Abs(calibration.ScreenHeight - frame.ScreenHeight) < 1;
        if (target == null)
        { ClearMovement(); return Decision(null, 0, 0, "NoCurrentTarget", calibrated); }

        if (target.Id != lastTrackId) { ClearMovement(); lastTrackId = target.Id; }
        string proposed = Classify(target, frame.ScreenHeight);
        if (proposed != candidateContext || double.IsNaN(candidateSince)) { candidateContext = proposed; candidateSince = frame.TimeSeconds; }
        if (context != proposed && frame.TimeSeconds - candidateSince >= .25) context = proposed;
        var profile = profiles[context];
        double safeDt = discontinuity ? 0 : Math.Clamp(dt, 0, .1);
        double blend = 1 - Math.Exp(-safeDt / .2);
        gain += blend * (profile.Gain - gain);
        smoothing += blend * (profile.SmoothingSeconds - smoothing);

        double errorX = target.Detection.X - frame.CenterX;
        double errorY = target.Detection.Y + (options.AimPointHeightFraction - .5) * target.Detection.Height - frame.CenterY;
        double radius = Math.Max(4, Math.Min(target.Detection.Width, target.Detection.Height) / 2);
        double errorNorm = Math.Min(50, AdaptiveTargetTracker.Distance(errorX, errorY) / radius);
        double normalizedSpeed = Math.Min(20, target.SpeedPixelsPerSecond / frame.ScreenHeight);
        double weight = 1.0 / Math.Min(20000, profile.ObservationFrames + 1);
        profile = profile with
        {
            ObservationFrames = Math.Min(long.MaxValue - 1, profile.ObservationFrames) + 1,
            ObservedSeconds = profile.ObservedSeconds + safeDt,
            MeanErrorInTargetRadii = profile.MeanErrorInTargetRadii + weight * (errorNorm - profile.MeanErrorInTargetRadii),
            MeanScreenSpeedInHeightsPerSecond = profile.MeanScreenSpeedInHeightsPerSecond + weight * (normalizedSpeed - profile.MeanScreenSpeedInHeightsPerSecond)
        };
        profiles[context] = profile;

        if (!calibrated || !frame.OutputAllowed || !frame.ActivationHeld || target.ConsecutiveFrames < 3 || discontinuity)
        {
            ClearMovement();
            string status = calibrationSuspended ? "CameraGainChangedRecalibrate" : !calibrated ? "CalibrationRequiredForThisView" :
                !frame.OutputAllowed ? "ObservationOnly" : !frame.ActivationHeld ? "WaitingForActivation" : "ConfirmingTarget";
            return Decision(target, 0, 0, status, calibrated);
        }

        double deadzone = Math.Clamp(target.Detection.Width * .025, 1.5, 5);
        bool crossing = previousAssisted &&
            ((Math.Abs(previousErrorX) > deadzone && Math.Abs(errorX) > deadzone && Math.Sign(previousErrorX) != Math.Sign(errorX)) ||
             (Math.Abs(previousErrorY) > deadzone && Math.Abs(errorY) > deadzone && Math.Sign(previousErrorY) != Math.Sign(errorY)));
        int windowFrames = assistedWindows.GetValueOrDefault(context) + 1;
        int windowCrossings = crossingWindows.GetValueOrDefault(context) + (crossing ? 1 : 0);
        profile = profile with { AssistedFrames = profile.AssistedFrames + 1, ErrorSignCrossings = profile.ErrorSignCrossings + (crossing ? 1 : 0) };
        // Reduce, never amplify, after repeated measured sign reversals. Human and camera motion can
        // contribute, so this conservative response is explicitly not proof of improved player aim.
        if (windowFrames >= 60)
        {
            if (windowCrossings >= 4)
                profile = profile with { Gain = Math.Max(.06, profile.Gain * .9), SmoothingSeconds = Math.Min(.14, profile.SmoothingSeconds + .005),
                    AdjustmentEvidence = "RepeatedScreenErrorCrossingsDuringAssistance_CausalityUnverified" };
            windowFrames = windowCrossings = 0;
        }
        assistedWindows[context] = windowFrames;
        crossingWindows[context] = windowCrossings;
        profiles[context] = profile;
        previousErrorX = errorX; previousErrorY = errorY; previousAssisted = true;

        double rateX = Math.Abs(errorX) <= deadzone ? 0 : -errorX / calibration!.PixelsPerCountX * gain * 60;
        double rateY = Math.Abs(errorY) <= deadzone ? 0 : -errorY / calibration!.PixelsPerCountY * gain * 60;
        double alpha = 1 - Math.Exp(-safeDt / Math.Max(.015, smoothing));
        filteredRateX += alpha * (rateX - filteredRateX);
        filteredRateY += alpha * (rateY - filteredRateY);
        if (rateX == 0 || Math.Sign(filteredRateX) != Math.Sign(rateX)) { filteredRateX = 0; residualX = 0; }
        if (rateY == 0 || Math.Sign(filteredRateY) != Math.Sign(rateY)) { filteredRateY = 0; residualY = 0; }
        double rateNorm = AdaptiveTargetTracker.Distance(filteredRateX, filteredRateY);
        double scale = rateNorm > options.MaximumCountsPerSecond ? options.MaximumCountsPerSecond / rateNorm : 1;
        double proposedX = filteredRateX * scale * safeDt + residualX;
        double proposedY = filteredRateY * scale * safeDt + residualY;
        // Never cross the estimated remaining target error in one output step.
        proposedX = Math.Clamp(proposedX, -Math.Abs(errorX / calibration!.PixelsPerCountX), Math.Abs(errorX / calibration.PixelsPerCountX));
        proposedY = Math.Clamp(proposedY, -Math.Abs(errorY / calibration.PixelsPerCountY), Math.Abs(errorY / calibration.PixelsPerCountY));
        double norm = AdaptiveTargetTracker.Distance(proposedX, proposedY);
        if (norm > options.MaximumCountsPerFrame)
        { proposedX *= options.MaximumCountsPerFrame / norm; proposedY *= options.MaximumCountsPerFrame / norm; }
        int countsX = (int)proposedX, countsY = (int)proposedY;
        residualX = proposedX - countsX; residualY = proposedY - countsY;
        return Decision(target, countsX, countsY, "CalibratedAssistance", true);
    }

    private AdaptiveDecision Decision(TargetTrack? target, int x, int y, string status, bool calibrated) =>
        new(target?.Detection.SourceIndex, target?.Id, context, x, y, status, target, calibrated, gain, smoothing);

    public AdaptivePlayerState Snapshot(string playerKey = "local") => new()
    {
        PlayerKey = string.IsNullOrWhiteSpace(playerKey) ? "local" : playerKey,
        Calibration = calibrationSuspended ? null : calibration,
        Profiles = profiles.Values.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray()
    };

    public void Reset()
    {
        tracker.Reset();
        lastTime = double.NaN;
        lastTrackId = 0;
        candidateContext = context;
        candidateSince = double.NaN;
        ClearMovement();
    }

    private void ClearMovement()
    {
        filteredRateX = filteredRateY = residualX = residualY = 0;
        previousAssisted = false;
    }

    private static string Classify(TargetTrack target, double screenHeight)
    {
        double ratio = target.Detection.Height / screenHeight;
        var size = ratio < .09 ? ApparentSize.Small : ratio > .23 ? ApparentSize.Large : ApparentSize.Medium;
        double speed = target.SpeedPixelsPerSecond / screenHeight;
        var motion = speed < .08 ? ScreenMotion.Slow : speed > .55 ? ScreenMotion.Fast : ScreenMotion.Moving;
        return $"{size}/{motion}";
    }

    private static ContextProfile CreateDefault(ApparentSize size, ScreenMotion motion) => new()
    {
        Key = $"{size}/{motion}",
        Gain = (size == ApparentSize.Small ? .11 : size == ApparentSize.Large ? .18 : .15) *
            (motion == ScreenMotion.Fast ? .85 : 1),
        SmoothingSeconds = motion == ScreenMotion.Fast ? .035 : size == ApparentSize.Small ? .09 : .06
    };
}
