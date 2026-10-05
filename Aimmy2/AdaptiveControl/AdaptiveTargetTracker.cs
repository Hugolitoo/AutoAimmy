namespace Aimmy2.AdaptiveControl;

/// <summary>Short-lived visual identities. Neither identity nor enemy class is ground truth.</summary>
public sealed class AdaptiveTargetTracker
{
    private sealed class Track
    {
        public long Id;
        public required DetectionSample Detection;
        public double LastSeen, FirstSeen, Vx, Vy;
        public int Consecutive;
        public bool Visible;
        public TargetTrack Snapshot => new(Id, Detection, LastSeen, Vx, Vy, Consecutive, LastSeen - FirstSeen);
    }

    private readonly AdaptiveControlOptions options;
    private readonly List<Track> tracks = new();
    private long nextId, selectedId, challengerId;
    private int challengerFrames;
    private double lastTime = double.NaN, selectedSince;

    public AdaptiveTargetTracker(AdaptiveControlOptions? options = null)
    {
        this.options = options ?? new();
        this.options.Validate();
    }

    public IReadOnlyList<TargetTrack> VisibleTracks { get; private set; } = Array.Empty<TargetTrack>();

    public TargetTrack? Update(double timeSeconds, IReadOnlyList<DetectionSample> detections, double centerX, double centerY)
    {
        if (!double.IsFinite(timeSeconds) || !double.IsFinite(centerX) || !double.IsFinite(centerY))
        { Reset(); return null; }
        if (!double.IsNaN(lastTime) && (timeSeconds <= lastTime || timeSeconds - lastTime > options.MaximumFrameGapSeconds))
            Reset();
        lastTime = timeSeconds;
        tracks.RemoveAll(t => timeSeconds - t.LastSeen > options.RetainSeconds);
        foreach (var t in tracks) t.Visible = false;
        var candidates = SuppressDuplicates(detections, options.MinimumConfidence);
        var pairs = new List<(Track Track, int Index, double Cost)>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var d = candidates[i];
            foreach (var t in tracks)
            {
                if (d.ClassId != t.Detection.ClassId) continue;
                double ratio = Math.Min(d.Width * d.Height, t.Detection.Width * t.Detection.Height) /
                    Math.Max(d.Width * d.Height, t.Detection.Width * t.Detection.Height);
                if (ratio < .35) continue;
                double dt = timeSeconds - t.LastSeen;
                double px = t.Detection.X + t.Vx * Math.Min(dt, .075);
                double py = t.Detection.Y + t.Vy * Math.Min(dt, .075);
                double distance = Distance(d.X - px, d.Y - py);
                double gate = Math.Max(28, Math.Max(t.Detection.Width, t.Detection.Height) * .7) + Math.Min(55, dt * 400);
                if (distance > gate) continue;
                pairs.Add((t, i, distance / gate + (1 - ratio) * .3));
            }
        }
        var matched = new HashSet<int>();
        foreach (var pair in pairs.OrderBy(p => p.Cost))
        {
            if (pair.Track.Visible || !matched.Add(pair.Index)) continue;
            var t = pair.Track;
            var d = candidates[pair.Index];
            double dt = timeSeconds - t.LastSeen;
            bool continuous = dt is > 0 and <= .075;
            if (continuous)
            {
                double alpha = 1 - Math.Exp(-dt / .055);
                t.Vx += alpha * (Math.Clamp((d.X - t.Detection.X) / dt, -4000, 4000) - t.Vx);
                t.Vy += alpha * (Math.Clamp((d.Y - t.Detection.Y) / dt, -4000, 4000) - t.Vy);
            }
            else { t.Vx = t.Vy = 0; }
            t.Consecutive = continuous ? t.Consecutive + 1 : 1;
            t.Detection = d;
            t.LastSeen = timeSeconds;
            t.Visible = true;
        }
        for (int i = 0; i < candidates.Count; i++)
        {
            if (matched.Contains(i)) continue;
            tracks.Add(new Track { Id = ++nextId, Detection = candidates[i], FirstSeen = timeSeconds,
                LastSeen = timeSeconds, Consecutive = 1, Visible = true });
        }
        VisibleTracks = tracks.Where(t => t.Visible).Select(t => t.Snapshot).ToArray();
        bool InRange(Track t) => Distance(t.Detection.X - centerX, t.Detection.Y - centerY) <= options.MaximumActivationRadiusPixels;
        double Score(Track t) => Distance(t.Detection.X - centerX, t.Detection.Y - centerY) / Math.Max(.4, t.Detection.Confidence);
        var best = tracks.Where(t => t.Visible && InRange(t)).MinBy(Score);
        var current = tracks.FirstOrDefault(t => t.Id == selectedId);
        if (current != null && !current.Visible)
        {
            challengerId = 0; challengerFrames = 0;
            // Retain identity briefly, but never emit a prediction through an occlusion.
            return null;
        }
        if (current == null || !InRange(current))
        {
            Select(best, timeSeconds);
            return best?.Snapshot;
        }
        if (best != null && best.Id != current.Id && Score(best) + 12 < Score(current) * .6 && timeSeconds - selectedSince >= .15)
        {
            if (challengerId == best.Id) challengerFrames++;
            else { challengerId = best.Id; challengerFrames = 1; }
            if (challengerFrames >= 3) { Select(best, timeSeconds); current = best; }
        }
        else { challengerId = 0; challengerFrames = 0; }
        return current.Snapshot;
    }

    private void Select(Track? track, double time)
    {
        selectedId = track?.Id ?? 0;
        selectedSince = time;
        challengerId = 0;
        challengerFrames = 0;
    }

    public void Reset()
    {
        tracks.Clear();
        selectedId = challengerId = 0;
        challengerFrames = 0;
        lastTime = double.NaN;
        VisibleTracks = Array.Empty<TargetTrack>();
    }

    public static IReadOnlyList<DetectionSample> SuppressDuplicates(IReadOnlyList<DetectionSample> detections,
        double minimumConfidence = .45, double iouThreshold = .55)
    {
        var result = new List<DetectionSample>();
        foreach (var candidate in detections.Where(d => d.IsValid && d.Confidence >= minimumConfidence)
            .OrderByDescending(d => d.Confidence).Take(512))
            if (!result.Any(d => d.ClassId == candidate.ClassId && IntersectionOverUnion(d, candidate) >= iouThreshold))
                result.Add(candidate);
        return result;
    }

    public static double IntersectionOverUnion(DetectionSample a, DetectionSample b)
    {
        double left = Math.Max(a.X - a.Width / 2, b.X - b.Width / 2);
        double right = Math.Min(a.X + a.Width / 2, b.X + b.Width / 2);
        double top = Math.Max(a.Y - a.Height / 2, b.Y - b.Height / 2);
        double bottom = Math.Min(a.Y + a.Height / 2, b.Y + b.Height / 2);
        double intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        return intersection / Math.Max(1, a.Width * a.Height + b.Width * b.Height - intersection);
    }

    internal static double Distance(double x, double y) => Math.Sqrt(x * x + y * y);
}
