namespace Aimmy2.Adaptive;

public sealed record MetricDistribution(int Count, double Mean, double P10, double Median, double P90)
{
    public static MetricDistribution? From(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (sorted.Length == 0) return null;
        double Quantile(double p)
        {
            double index = (sorted.Length - 1) * p;
            int lower = (int)index;
            return sorted[lower] + (sorted[Math.Min(lower + 1, sorted.Length - 1)] - sorted[lower]) * (index - lower);
        }
        return new(sorted.Length, sorted.Average(), Quantile(.1), Quantile(.5), Quantile(.9));
    }
}

/// <summary>Session distributions, not yet a permanent learned player model.</summary>
public sealed record PlayerStyleProfile(int Engagements, int TruncatedEngagements,
    IReadOnlyDictionary<string, MetricDistribution?> Metrics,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, MetricDistribution?>> Contexts);
