namespace Aimmy2.AdaptiveControl;

public sealed record CalibrationObservation(double TimeSeconds, long TrackId, double TargetX, double TargetY,
    double RawDeltaX, double RawDeltaY, bool StationaryTargetConfirmed, bool OutputWasActive = false,
    double TargetHeight = 0);

public sealed record CalibrationAxisProgress(int Samples, int CleanSamples, int PositiveSamples, int NegativeSamples,
    double TotalCounts, double PixelsPerCount, double Fit, double RelativeSpread, string MissingReason, bool Complete);

public sealed record CalibrationProgress(CalibrationAxisProgress X, CalibrationAxisProgress Y,
    double DurationSeconds, string Status, bool Complete);

public sealed record CalibrationResult
{
    public bool Success { get; init; }
    public string Status { get; init; } = "NotStarted";
    public string ContextKey { get; init; } = "default";
    public double ScreenHeight { get; init; }
    public double PixelsPerCountX { get; init; }
    public double PixelsPerCountY { get; init; }
    public double FitX { get; init; }
    public double FitY { get; init; }
    public double RelativeSpreadX { get; init; }
    public double RelativeSpreadY { get; init; }
    public int SamplesX { get; init; }
    public int SamplesY { get; init; }
    public double DurationSeconds { get; init; }
    public DateTime MeasuredUtc { get; init; }
    public string Method { get; init; } = "GuidedStationaryTargetRawInput";

    public bool IsUsable => Success && Method == "GuidedStationaryTargetRawInput" &&
        !string.IsNullOrWhiteSpace(ContextKey) && ContextKey.Length <= 120 &&
        double.IsFinite(ScreenHeight) && ScreenHeight is >= 200 and <= 10000 &&
        ValidGain(PixelsPerCountX) && ValidGain(PixelsPerCountY) &&
        double.IsFinite(FitX) && double.IsFinite(FitY) && FitX is >= .88 and <= 1 && FitY is >= .88 and <= 1 &&
        double.IsFinite(RelativeSpreadX) && double.IsFinite(RelativeSpreadY) && RelativeSpreadX is >= 0 and <= .25 && RelativeSpreadY is >= 0 and <= .25 &&
        SamplesX >= 12 && SamplesY >= 12 && double.IsFinite(DurationSeconds) && DurationSeconds >= 2;
    internal static bool ValidGain(double value) => double.IsFinite(value) && Math.Abs(value) is >= .02 and <= 40;
}

/// <summary>
/// Passive calibration while a user pans around a stationary target without firing or moving their character.
/// The stationary-target premise is user-confirmed; screen pixels cannot establish it on their own.
/// Measures the current view's camera response, never hardware DPI, degrees, recoil or hits.
/// </summary>
public sealed class GuidedCalibration
{
    public const double MaximumObservationGapSeconds = .5;
    private const double MinimumWindowSeconds = .10, MaximumWindowSeconds = .60, MinimumDisplacementPixels = 2;
    private sealed record Pair(double Counts, double Pixels);
    private readonly List<Pair> xPairs = new(), yPairs = new();
    private readonly string contextKey;
    private readonly double screenHeight;
    private CalibrationObservation? previous, anchor;
    private double accumulatedX, accumulatedY, start = double.NaN, last = double.NaN;
    private string lastIssue = "MoveHorizontallyThenVerticallyBothDirections";

    public GuidedCalibration(string contextKey, double screenHeight)
    {
        if (string.IsNullOrWhiteSpace(contextKey) || contextKey.Length > 120 ||
            !double.IsFinite(screenHeight) || screenHeight is < 200 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(contextKey));
        this.contextKey = contextKey;
        this.screenHeight = screenHeight;
    }

    public int SamplesX => xPairs.Count;
    public int SamplesY => yPairs.Count;
    public string Status => Progress.Status;
    public CalibrationProgress Progress
    {
        get
        {
            var x = Fit(xPairs);
            var y = Fit(yPairs);
            double duration = double.IsNaN(start) || double.IsNaN(last) ? 0 : Math.Max(0, last - start);
            bool success = x.Complete && y.Complete && duration >= 2;
            string status = success ? "CalibratedForCurrentView" :
                x.Complete && y.Complete ? "MoreObservationTimeRequired" :
                xPairs.Count >= 12 && yPairs.Count >= 12 && lastIssue == "MoveHorizontallyThenVerticallyBothDirections" ?
                    "InconsistentMotionRetryOnStationaryTarget" : lastIssue;
            return new(x, y, duration, status, success);
        }
    }

    public CalibrationResult Observe(CalibrationObservation observation)
    {
        if (!Valid(observation) || !observation.StationaryTargetConfirmed || observation.OutputWasActive)
        {
            BreakInterval();
            lastIssue = observation.OutputWasActive ? "OutputMustBeDisabled" : "StationaryTargetRequired";
            return Evaluate();
        }
        if (double.IsNaN(start)) start = observation.TimeSeconds;
        last = observation.TimeSeconds;
        if (previous == null || anchor == null || observation.TrackId != previous.TrackId ||
            observation.TimeSeconds <= previous.TimeSeconds || observation.TimeSeconds - previous.TimeSeconds > MaximumObservationGapSeconds ||
            (previous.TargetHeight > 0 && observation.TargetHeight > 0 &&
                Math.Abs(observation.TargetHeight / previous.TargetHeight - 1) > .12))
        {
            previous = anchor = observation;
            accumulatedX = accumulatedY = 0;
            lastIssue = "TargetMustStayVisibleAndSameSize";
            return Evaluate();
        }

        int accumulatedAxis = DominantAxis(accumulatedX, accumulatedY);
        int observationAxis = DominantAxis(observation.RawDeltaX, observation.RawDeltaY);
        bool directionChanged =
            Reverses(accumulatedX, observation.RawDeltaX) && (accumulatedAxis == 1 || observationAxis == 1) ||
            Reverses(accumulatedY, observation.RawDeltaY) && (accumulatedAxis == 2 || observationAxis == 2) ||
            accumulatedAxis != 0 && observationAxis != 0 && accumulatedAxis != observationAxis;
        if (directionChanged || observation.TimeSeconds - anchor.TimeSeconds > MaximumWindowSeconds + 1e-9)
        {
            // Finish only the preceding direction. Its endpoint and raw counts both stop at
            // the previous image, before this observation's reverse movement began.
            Collect(previous);
            RestartWindow(previous);
        }
        accumulatedX += observation.RawDeltaX;
        accumulatedY += observation.RawDeltaY;
        previous = observation;
        double dt = observation.TimeSeconds - anchor.TimeSeconds;
        // Keep small coherent movements until there is measurable evidence, with a bounded
        // window so camera drift or a changed view cannot be accumulated indefinitely.
        if (Collect(observation) || dt >= MaximumWindowSeconds - 1e-9) RestartWindow(observation);
        lastIssue = "MoveHorizontallyThenVerticallyBothDirections";
        return Evaluate();
    }

    private bool Collect(CalibrationObservation endpoint)
    {
        if (anchor == null) return false;
        double dt = endpoint.TimeSeconds - anchor.TimeSeconds;
        if (dt < MinimumWindowSeconds - 1e-9 || dt > MaximumWindowSeconds + 1e-9) return false;
        double dx = endpoint.TargetX - anchor.TargetX, dy = endpoint.TargetY - anchor.TargetY;
        bool horizontal = Math.Abs(accumulatedX) >= 4 && Math.Abs(accumulatedX) >= Math.Abs(accumulatedY) * 2.5 &&
            Math.Abs(dx) >= MinimumDisplacementPixels && Math.Abs(dx) <= 180 && Math.Abs(dy) <= Math.Max(12, Math.Abs(dx) * .35);
        bool vertical = Math.Abs(accumulatedY) >= 4 && Math.Abs(accumulatedY) >= Math.Abs(accumulatedX) * 2.5 &&
            Math.Abs(dy) >= MinimumDisplacementPixels && Math.Abs(dy) <= 180 && Math.Abs(dx) <= Math.Max(12, Math.Abs(dy) * .35);
        if (horizontal) Add(xPairs, new(accumulatedX, dx));
        if (vertical) Add(yPairs, new(accumulatedY, dy));
        return horizontal || vertical;
    }

    private void RestartWindow(CalibrationObservation observation)
    { anchor = observation; accumulatedX = accumulatedY = 0; }

    private static int DominantAxis(double x, double y) => Math.Abs(x) > 0 && Math.Abs(x) >= Math.Abs(y) * 2.5 ? 1 :
        Math.Abs(y) > 0 && Math.Abs(y) >= Math.Abs(x) * 2.5 ? 2 : 0;
    private static bool Reverses(double accumulated, double delta) => accumulated != 0 && delta != 0 &&
        Math.Sign(accumulated) != Math.Sign(delta);

    private static void Add(List<Pair> values, Pair pair)
    {
        values.Add(pair);
        if (values.Count > 256) values.RemoveAt(0);
    }

    public CalibrationResult Evaluate()
    {
        var progress = Progress;
        var x = progress.X;
        var y = progress.Y;
        return new CalibrationResult
        {
            Success = progress.Complete,
            Status = progress.Status,
            ContextKey = contextKey, ScreenHeight = screenHeight,
            PixelsPerCountX = x.PixelsPerCount, PixelsPerCountY = y.PixelsPerCount,
            FitX = x.Fit, FitY = y.Fit, SamplesX = x.CleanSamples, SamplesY = y.CleanSamples,
            RelativeSpreadX = x.RelativeSpread, RelativeSpreadY = y.RelativeSpread,
            DurationSeconds = progress.DurationSeconds, MeasuredUtc = DateTime.UtcNow
        };
    }

    private static CalibrationAxisProgress Fit(List<Pair> pairs)
    {
        if (pairs.Count == 0) return new(0, 0, 0, 0, 0, 0, 0, 0, "SamplesRequired", false);
        double Median(IEnumerable<double> values)
        {
            double[] sorted = values.Order().ToArray();
            return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] :
                (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        }
        double medianGain = Median(pairs.Select(p => p.Pixels / p.Counts));
        double mad = Median(pairs.Select(p => Math.Abs(p.Pixels / p.Counts - medianGain)));
        double tolerance = Math.Max(.06 * Math.Abs(medianGain), 3.5 * mad);
        var clean = pairs.Where(p => Math.Abs(p.Pixels / p.Counts - medianGain) <= tolerance).ToArray();
        double denominator = clean.Sum(p => p.Counts * p.Counts);
        double gain = denominator > 0 ? clean.Sum(p => p.Counts * p.Pixels) / denominator : 0;
        double energy = clean.Sum(p => p.Pixels * p.Pixels);
        double residual = clean.Sum(p => Math.Pow(p.Pixels - gain * p.Counts, 2));
        double quality = energy > 0 ? Math.Clamp(1 - residual / energy, 0, 1) : 0;
        var relativeErrors = clean.Select(p => Math.Abs(p.Pixels / p.Counts - gain) / Math.Max(.001, Math.Abs(gain))).Order().ToArray();
        double spread = relativeErrors.Length > 0 ? relativeErrors[(int)Math.Floor((relativeErrors.Length - 1) * .9)] : 1;
        int positive = clean.Count(p => p.Counts > 0), negative = clean.Count(p => p.Counts < 0);
        double totalCounts = clean.Sum(p => Math.Abs(p.Counts));
        string missingReason = pairs.Count >= 12 && clean.Length < pairs.Count * .75 ? "TooManyOutliers" :
            clean.Length < 12 ? "SamplesRequired" :
            quality < .88 || spread > .25 ? "InconsistentMotion" :
            positive < 3 || negative < 3 ? "BothDirectionsRequired" :
            totalCounts < 160 ? "MoreMovementRequired" :
            !CalibrationResult.ValidGain(gain) ? "InvalidGain" : "Ready";
        return new(pairs.Count, clean.Length, positive, negative, totalCounts, gain, quality, spread,
            missingReason, missingReason == "Ready");
    }

    public void BreakInterval() { previous = anchor = null; accumulatedX = accumulatedY = 0; }

    private static bool Valid(CalibrationObservation o) => double.IsFinite(o.TimeSeconds) && o.TrackId > 0 &&
        double.IsFinite(o.TargetX) && double.IsFinite(o.TargetY) && double.IsFinite(o.RawDeltaX) &&
        double.IsFinite(o.RawDeltaY) && Math.Abs(o.RawDeltaX) <= 5000 && Math.Abs(o.RawDeltaY) <= 5000;
}
