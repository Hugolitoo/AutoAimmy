namespace Aimmy2.AdaptiveControl;

/// <summary>Centers and bounds are absolute screen pixels, before any Aimmy movement conversion.</summary>
public sealed record DetectionSample(int SourceIndex, double X, double Y, double Width, double Height,
    double Confidence, int ClassId = 0)
{
    public bool IsValid => SourceIndex >= 0 && double.IsFinite(X) && double.IsFinite(Y) &&
        double.IsFinite(Width) && double.IsFinite(Height) && Width is > 1 and < 10000 &&
        Height is > 1 and < 10000 && double.IsFinite(Confidence) && Confidence is >= 0 and <= 1;
}

public enum ApparentSize { Small, Medium, Large }
public enum ScreenMotion { Slow, Moving, Fast }

/// <summary>Screen motion contains camera motion. Size is an image ratio, never physical distance.</summary>
public sealed record TargetTrack(long Id, DetectionSample Detection, double ObservedAt,
    double VelocityX, double VelocityY, int ConsecutiveFrames, double AgeSeconds)
{
    public double SpeedPixelsPerSecond => Math.Sqrt(VelocityX * VelocityX + VelocityY * VelocityY);
}

public sealed record AdaptiveFrame(double TimeSeconds, double CenterX, double CenterY, double ScreenHeight,
    IReadOnlyList<DetectionSample> Detections, bool ActivationHeld = false, bool OutputAllowed = false,
    string CalibrationContextKey = "default", double SceneVelocityX = 0, double SceneVelocityY = 0,
    bool SceneMotionReliable = false, double RecoilPixelsPerSecondY = 0);

/// <summary>Relative counts, not screen coordinates. The caller must still enforce its output guards.</summary>
public sealed record AdaptiveDecision(int? TargetSourceIndex, long? TrackId, string ContextKey,
    int CountsX, int CountsY, string Status, TargetTrack? Target, bool Calibrated,
    double Gain, double SmoothingSeconds)
{
    public bool HasCorrection => CountsX != 0 || CountsY != 0;
}

public sealed record AdaptiveControlOptions
{
    public double MinimumConfidence { get; init; } = .45;
    public double RetainSeconds { get; init; } = .15;
    public double MaximumFrameGapSeconds { get; init; } = .15;
    public double MaximumActivationRadiusPixels { get; init; } = 280;
    public double MaximumCountsPerSecond { get; init; } = 600;
    public int MaximumCountsPerFrame { get; init; } = 24;
    public double AimPointHeightFraction { get; init; } = .42;

    internal void Validate()
    {
        if (!double.IsFinite(MinimumConfidence) || MinimumConfidence is < .1 or > 1 ||
            !double.IsFinite(RetainSeconds) || RetainSeconds is < .03 or > .4 ||
            !double.IsFinite(MaximumFrameGapSeconds) || MaximumFrameGapSeconds is < .03 or > .3 ||
            !double.IsFinite(MaximumActivationRadiusPixels) || MaximumActivationRadiusPixels is < 20 or > 1000 ||
            !double.IsFinite(MaximumCountsPerSecond) || MaximumCountsPerSecond is < 1 or > 3000 ||
            MaximumCountsPerFrame is < 1 or > 100 || !double.IsFinite(AimPointHeightFraction) ||
            AimPointHeightFraction is < .2 or > .8)
            throw new ArgumentOutOfRangeException(nameof(AdaptiveControlOptions));
    }
}

public sealed record ContextProfile
{
    public string Key { get; init; } = "Medium/Slow";
    public double Gain { get; init; } = .16;
    public double SmoothingSeconds { get; init; } = .07;
    public long ObservationFrames { get; init; }
    public double ObservedSeconds { get; init; }
    public double MeanErrorInTargetRadii { get; init; }
    public double MeanScreenSpeedInHeightsPerSecond { get; init; }
    public long AssistedFrames { get; init; }
    public long ErrorSignCrossings { get; init; }
    public string AdjustmentEvidence { get; init; } = "ConservativeDefault";
    public double SizeFeature { get; init; }
    public double SpeedFeature { get; init; }
    public double HorizontalFeature { get; init; }
    public long FeatureSamples { get; init; }
    public long ComparedWindows { get; init; }
}

public sealed record AdaptivePlayerState
{
    public int Schema { get; init; } = 1;
    public string PlayerKey { get; init; } = "local";
    public CalibrationResult? Calibration { get; init; }
    public ContextProfile[] Profiles { get; init; } = Array.Empty<ContextProfile>();
}
