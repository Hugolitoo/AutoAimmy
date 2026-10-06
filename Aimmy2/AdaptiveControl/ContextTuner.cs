namespace Aimmy2.AdaptiveControl;

public sealed record TuningParameters(double Gain, double Smoothing, bool Candidate);
public sealed record TuningResult(bool Completed, bool Accepted, double Gain, double Smoothing, int Windows);

/// <summary>Bounded randomized comparisons. Screen error is evidence about tracking, never a hit metric.</summary>
public sealed class ContextTuner
{
    private readonly Random random = new(193);
    private readonly List<double> baseline = [], candidate = [];
    private double gain, smoothing, candidateGain, candidateSmoothing;
    private bool useCandidate;
    private int cycle;
    public TuningParameters BeginWindow(double currentGain, double currentSmoothing)
    {
        if (baseline.Count + candidate.Count == 0)
        {
            gain = currentGain; smoothing = currentSmoothing;
            candidateGain = Math.Clamp(gain * (cycle % 2 == 0 ? 1.1 : .9), .06, .22);
            candidateSmoothing = Math.Clamp(smoothing * (cycle % 3 == 0 ? .9 : 1.1), .025, .14);
        }
        useCandidate = baseline.Count >= 16 ? true : candidate.Count >= 16 ? false : random.Next(2) == 0;
        return new(useCandidate ? candidateGain : gain, useCandidate ? candidateSmoothing : smoothing, useCandidate);
    }
    public TuningResult EndWindow(double meanError, int crossings)
    {
        if (!double.IsFinite(meanError) || meanError is < 0 or > 50 || crossings is < 0 or > 100)
            return new(false, false, gain, smoothing, baseline.Count + candidate.Count);
        (useCandidate ? candidate : baseline).Add(meanError + crossings * .12);
        if (baseline.Count < 16 || candidate.Count < 16) return new(false, false, gain, smoothing, baseline.Count + candidate.Count);
        double AverageVariance(List<double> scores)
        {
            double mean = scores.Average(); return scores.Sum(x => (x - mean) * (x - mean)) / (scores.Count - 1);
        }
        double before = baseline.Average(), after = candidate.Average();
        double uncertainty = 1.96 * Math.Sqrt(AverageVariance(baseline) / baseline.Count + AverageVariance(candidate) / candidate.Count);
        bool accepted = after < before * .92 && before - after > Math.Max(.02, uncertainty);
        var result = new TuningResult(true, accepted, accepted ? candidateGain : gain,
            accepted ? candidateSmoothing : smoothing, baseline.Count + candidate.Count);
        baseline.Clear(); candidate.Clear(); cycle++;
        return result;
    }
}
