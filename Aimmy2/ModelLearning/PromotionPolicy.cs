namespace Aimmy2.ModelLearning;

public static class PromotionPolicy
{
    public static string[] RejectionReasons(DetectionMetrics baseline, DetectionMetrics candidate)
    {
        var reasons = new List<string>();
        bool Invalid(DetectionMetrics metrics) => metrics.Images < 0 || metrics.GroundTruthBoxes < 0 || metrics.TruePositives < 0 ||
            metrics.FalsePositives < 0 || metrics.FalseNegatives < 0 || metrics.TruePositives + metrics.FalseNegatives != metrics.GroundTruthBoxes ||
            !double.IsFinite(metrics.MeanMilliseconds) || metrics.MeanMilliseconds < 0;
        if (Invalid(baseline) || Invalid(candidate)) return ["InvalidMetrics"];
        if (candidate.Images != baseline.Images || candidate.GroundTruthBoxes != baseline.GroundTruthBoxes) reasons.Add("DifferentValidationSets");
        if (candidate.Images < 20 || candidate.GroundTruthBoxes < 20) reasons.Add("ValidationSetTooSmall");
        if (candidate.F1 < baseline.F1 + .01) reasons.Add("NoMeasuredAccuracyGain");
        if (candidate.Precision < baseline.Precision - .01) reasons.Add("PrecisionRegression");
        if (candidate.Recall < baseline.Recall - .01) reasons.Add("RecallRegression");
        if (candidate.MeanMilliseconds > Math.Max(baseline.MeanMilliseconds * 1.2, baseline.MeanMilliseconds + 2)) reasons.Add("LatencyRegression");
        return reasons.ToArray();
    }
}

public sealed class MetricAccumulator
{
    public int Images { get; private set; }
    private int _targets, _tp, _fp, _fn;
    private double _milliseconds;
    public void Add(IEnumerable<LearningBox> groundTruth, IEnumerable<LearningBox> predictions, double milliseconds)
    {
        var labels = groundTruth.ToArray();
        var detected = predictions.OrderByDescending(x => x.Confidence).ToArray();
        if (labels.Any(x => !x.IsValid) || detected.Any(x => !x.IsValid) || !double.IsFinite(milliseconds) || milliseconds < 0)
            throw new ArgumentException("Invalid boxes or timing.");
        var matched = new HashSet<int>();
        foreach (var detection in detected)
        {
            var best = Enumerable.Range(0, labels.Length).Where(i => !matched.Contains(i) && labels[i].ClassId == detection.ClassId)
                .Select(i => (Index: i, IoU: IntersectionOverUnion(labels[i], detection))).OrderByDescending(x => x.IoU).FirstOrDefault((Index: -1, IoU: 0d));
            if (best.Index >= 0 && best.IoU >= .5) { matched.Add(best.Index); _tp++; } else _fp++;
        }
        _fn += labels.Length - matched.Count;
        _targets += labels.Length;
        _milliseconds += milliseconds;
        Images++;
    }
    public DetectionMetrics Result() => new(Images, _targets, _tp, _fp, _fn, Images == 0 ? 0 : _milliseconds / Images);
    public static double IntersectionOverUnion(LearningBox a, LearningBox b)
    {
        var width = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X));
        var height = Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        var intersection = width * height;
        return intersection / (a.Width * a.Height + b.Width * b.Height - intersection);
    }
}
