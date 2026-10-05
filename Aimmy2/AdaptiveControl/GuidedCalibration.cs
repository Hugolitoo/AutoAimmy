namespace Aimmy2.AdaptiveControl;

public sealed record CalibrationObservation(double TimeSeconds, long TrackId, double TargetX, double TargetY,
    double RawDeltaX, double RawDeltaY, bool StationaryTargetConfirmed, bool OutputWasActive = false,
    double TargetHeight = 0);

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
    public string Status => Evaluate().Status;

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
            observation.TimeSeconds <= previous.TimeSeconds || observation.TimeSeconds - previous.TimeSeconds > .15 ||
            (previous.TargetHeight > 0 && observation.TargetHeight > 0 &&
                Math.Abs(observation.TargetHeight / previous.TargetHeight - 1) > .12))
        {
            previous = anchor = observation;
            accumulatedX = accumulatedY = 0;
            lastIssue = "TargetMustStayVisibleAndSameSize";
            return Evaluate();
        }
        accumulatedX += observation.RawDeltaX;
        accumulatedY += observation.RawDeltaY;
        previous = observation;
        double dt = observation.TimeSeconds - anchor.TimeSeconds;
        if (dt >= .10)
        {
            double dx = observation.TargetX - anchor.TargetX, dy = observation.TargetY - anchor.TargetY;
            if (dt <= .25)
            {
                if (Math.Abs(accumulatedX) >= 4 && Math.Abs(accumulatedX) >= Math.Abs(accumulatedY) * 2.5 &&
                    Math.Abs(dx) is >= 1 and <= 180 && Math.Abs(dy) <= Math.Max(12, Math.Abs(dx) * .35))
                    Add(xPairs, new(accumulatedX, dx));
                if (Math.Abs(accumulatedY) >= 4 && Math.Abs(accumulatedY) >= Math.Abs(accumulatedX) * 2.5 &&
                    Math.Abs(dy) is >= 1 and <= 180 && Math.Abs(dx) <= Math.Max(12, Math.Abs(dy) * .35))
                    Add(yPairs, new(accumulatedY, dy));
            }
            anchor = observation;
            accumulatedX = accumulatedY = 0;
        }
        lastIssue = "MoveHorizontallyThenVerticallyBothDirections";
        return Evaluate();
    }

    private static void Add(List<Pair> values, Pair pair)
    {
        values.Add(pair);
        if (values.Count > 256) values.RemoveAt(0);
    }

    public CalibrationResult Evaluate()
    {
        var x = Fit(xPairs);
        var y = Fit(yPairs);
        double duration = double.IsNaN(start) || double.IsNaN(last) ? 0 : Math.Max(0, last - start);
        bool success = x.Good && y.Good && duration >= 2;
        return new CalibrationResult
        {
            Success = success,
            Status = success ? "CalibratedForCurrentView" : xPairs.Count >= 12 && yPairs.Count >= 12 ?
                "InconsistentMotionRetryOnStationaryTarget" : lastIssue,
            ContextKey = contextKey, ScreenHeight = screenHeight,
            PixelsPerCountX = x.Gain, PixelsPerCountY = y.Gain,
            FitX = x.Quality, FitY = y.Quality, SamplesX = x.Count, SamplesY = y.Count,
            RelativeSpreadX = x.Spread, RelativeSpreadY = y.Spread,
            DurationSeconds = duration, MeasuredUtc = DateTime.UtcNow
        };
    }

    private static (double Gain, double Quality, int Count, double Spread, bool Good) Fit(List<Pair> pairs)
    {
        if (pairs.Count == 0) return (0, 0, 0, 0, false);
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
        bool good = clean.Length >= 12 && clean.Length >= pairs.Count * .75 && quality >= .88 &&
            spread <= .25 &&
            clean.Count(p => p.Counts > 0) >= 3 && clean.Count(p => p.Counts < 0) >= 3 &&
            clean.Sum(p => Math.Abs(p.Counts)) >= 160 && CalibrationResult.ValidGain(gain);
        return (gain, quality, clean.Length, spread, good);
    }

    public void BreakInterval() { previous = anchor = null; accumulatedX = accumulatedY = 0; }

    private static bool Valid(CalibrationObservation o) => double.IsFinite(o.TimeSeconds) && o.TrackId > 0 &&
        double.IsFinite(o.TargetX) && double.IsFinite(o.TargetY) && double.IsFinite(o.RawDeltaX) &&
        double.IsFinite(o.RawDeltaY) && Math.Abs(o.RawDeltaX) <= 5000 && Math.Abs(o.RawDeltaY) <= 5000;
}
