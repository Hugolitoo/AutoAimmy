namespace Aimmy2.Adaptive;

public sealed record ImpactCue(bool ProbableHeadMarker = false, bool ProbableBlood = false,
    double MarkerStrength = 0, double BloodIncrease = 0)
{
    public bool Any => ProbableHeadMarker || ProbableBlood;
    public string Evidence => "UnverifiedVisualCue";
}

/// <summary>Temporal visual evidence, never a confirmed hit or a detector annotation.</summary>
public sealed class ImpactCueDetector
{
    private double lastTime = double.NaN, lastCue = -10, markerBaseline, priorBlood, priorGlobalRed;
    private int warmup;
    public void Reset() { lastTime = double.NaN; lastCue = -10; warmup = 0; }
    public ImpactCue Observe(double time, double marker, double targetRed, double globalRed,
        bool firingRecently, bool stableTarget)
    {
        if (!new[] { time, marker, targetRed, globalRed }.All(double.IsFinite) || marker is < 0 or > 1 ||
            targetRed is < 0 or > 1 || globalRed is < 0 or > 1) { Reset(); return new(); }
        if (!double.IsFinite(lastTime) || time <= lastTime || time - lastTime > .3)
        { warmup = 0; markerBaseline = marker; priorBlood = targetRed; priorGlobalRed = globalRed; }
        double markerRise = marker - markerBaseline, redRise = targetRed - priorBlood;
        bool eligible = warmup >= 3 && firingRecently && stableTarget && time - lastCue >= .4;
        bool head = eligible && marker >= .25 && markerRise >= .22;
        bool blood = eligible && targetRed >= .04 && redRise >= .025 && globalRed - priorGlobalRed < .012;
        if (head || blood) lastCue = time;
        markerBaseline = .85 * markerBaseline + .15 * marker;
        priorBlood = targetRed; priorGlobalRed = globalRed; lastTime = time; warmup++;
        return new(head, blood, marker, Math.Max(0, redRise));
    }

    // A transient X-shaped bright/red flash around the reticle. A static optic, plus sign,
    // or uniformly bright scene must not satisfy this four-arm contrast test.
    public static double MarkerStrength(ReadOnlySpan<byte> rgb, int size)
    {
        if (size < 32 || size > 128 || rgb.Length != size * size * 3) return 0;
        var on = new double[4]; var count = new int[4]; double off = 0; int offCount = 0;
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            double dx = (x + .5 - size / 2d) / size, dy = (y + .5 - size / 2d) / size;
            double radius = Math.Sqrt(dx * dx + dy * dy);
            if (radius < .10 || radius > .43) continue;
            int i = (y * size + x) * 3;
            double bright = Math.Min(rgb[i], Math.Min(rgb[i + 1], rgb[i + 2])) / 255d;
            double red = rgb[i] > 160 && rgb[i] > 1.45 * rgb[i + 1] && rgb[i] > 1.35 * rgb[i + 2] ? rgb[i] / 255d : 0;
            double value = Math.Max(bright, red);
            if (Math.Abs(Math.Abs(dx) - Math.Abs(dy)) < .035)
            { int quadrant = (dx > 0 ? 1 : 0) + (dy > 0 ? 2 : 0); on[quadrant] += value; count[quadrant]++; }
            else if (Math.Abs(dx) > .06 && Math.Abs(dy) > .06) { off += value; offCount++; }
        }
        double weakest = Enumerable.Range(0, 4).Min(i => count[i] == 0 ? 0 : on[i] / count[i]);
        return Math.Clamp(weakest - (offCount == 0 ? 0 : off / offCount), 0, 1);
    }
}
