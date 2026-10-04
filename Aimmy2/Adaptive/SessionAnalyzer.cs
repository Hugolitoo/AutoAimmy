using System.Text;

namespace Aimmy2.Adaptive;

/// <summary>Consumed only by the recorder worker; stores features instead of full sessions in RAM.</summary>
public sealed class SessionAnalyzer
{
    private readonly List<EngagementFeatures> features = new();
    public void Add(EngagementFeatures value) => features.Add(value);

    public PlayerStyleProfile Analyze()
    {
        Dictionary<string, MetricDistribution?> Summarize(IEnumerable<EngagementFeatures> source)
        {
            var all = source.ToArray();
            return all.SelectMany(f => f.Metrics.Keys).Distinct().ToDictionary(k => k,
                k => MetricDistribution.From(all.Select(f => f.Metrics.GetValueOrDefault(k)).Where(v => v.HasValue).Select(v => v!.Value)));
        }
        // Censored/gapped encounters remain in the raw feature export, excluded from baseline distributions.
        var valid = features.Where(f => !f.Truncated).ToArray();
        var contexts = valid.GroupBy(f => f.Context).ToDictionary(g => g.Key,
            g => (IReadOnlyDictionary<string, MetricDistribution?>)Summarize(g));
        return new(features.Count, features.Count(f => f.Truncated), Summarize(valid), contexts);
    }

    public static string Report(PlayerStyleProfile profile, long droppedEvents)
    {
        var text = new StringBuilder("SESSION ANALYSIS — observation only\n");
        text.AppendLine($"Engagements: {profile.Engagements}; truncated: {profile.TruncatedEngagements}; dropped samples: {droppedEvents}");
        foreach (var (name, d) in profile.Metrics)
            text.AppendLine(d == null ? $"{name}: unavailable" : $"{name}: median {d.Median:F2}, p10–p90 {d.P10:F2}–{d.P90:F2}, mean {d.Mean:F2}, n={d.Count}");
        text.AppendLine("Cursor telemetry only. Reaction is movement-onset latency after first detection, not cognitive reaction time. Clicks are not confirmed hits.");
        return text.ToString();
    }
}
