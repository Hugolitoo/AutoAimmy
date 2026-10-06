namespace Aimmy2.AdaptiveControl;

public sealed record RecoilState(double[] Samples, long ObservedShots);

/// <summary>Repeated camera residuals correlated with ammo decreases. Not an impact or kill detector.</summary>
public sealed class RecoilEstimator
{
    private readonly Queue<double> samples = new();
    public long ObservedShots { get; private set; }
    public RecoilEstimator(RecoilState? state = null)
    {
        if (state == null) return;
        foreach (double value in (state.Samples ?? []).TakeLast(12)) if (double.IsFinite(value) && value is > .0005 and < .1) samples.Enqueue(value);
        ObservedShots = Math.Clamp(state.ObservedShots, 0, 1_000_000_000);
    }
    public bool Observe(int estimatedShots, double residualYInScreenHeights, bool independent, bool cameraReliable)
    {
        if (!independent || !cameraReliable || estimatedShots is < 2 or > 20 || !double.IsFinite(residualYInScreenHeights)) return false;
        double value = residualYInScreenHeights / estimatedShots;
        if (value is <= .0005 or >= .1) return false;
        samples.Enqueue(value); if (samples.Count > 12) samples.Dequeue();
        ObservedShots = Math.Min(1_000_000_000, ObservedShots + estimatedShots);
        return true;
    }
    public int Windows => samples.Count;
    public double MeanKick
    {
        get { var ordered = samples.Order().ToArray(); int trim = ordered.Length >= 8 ? 1 : 0;
            return ordered.Length == 0 ? 0 : ordered.Skip(trim).Take(ordered.Length - trim * 2).Average(); }
    }
    public bool Reliable
    {
        get { double mean = MeanKick; return samples.Count >= 6 && mean > 0 &&
            Math.Sqrt(samples.Average(v => Math.Pow(v - mean, 2))) / mean < .3; }
    }
    public RecoilState Snapshot() => new(samples.ToArray(), ObservedShots);
}
