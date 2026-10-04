namespace Aimmy2.Adaptive;

/// <summary>Single-consumer state machine. No disk or input operations.</summary>
public sealed class EngagementSegmenter
{
    private readonly List<GameplayEvent> samples = new();
    private long? targetId;
    private double lastSeen;
    private bool previousClick;
    private bool waitingForRelease;
    public event Action<Engagement>? Completed;
    public double LostTargetGraceSeconds { get; init; } = .15;
    public double MaxDurationSeconds { get; init; } = 10;

    public void Push(GameplayEvent sample)
    {
        bool clickEdge = sample.LeftClick && !previousClick;
        previousClick = sample.LeftClick;
        if (waitingForRelease)
        {
            if (sample.LeftClick) return;
            waitingForRelease = false;
        }
        if (samples.Count > 0 && (sample.Timestamp <= samples[^1].Timestamp || sample.Timestamp - samples[^1].Timestamp > .1 ||
            (sample.Sequence > 0 && samples[^1].Sequence > 0 && sample.Sequence != samples[^1].Sequence + 1)))
            Finish("SamplingGap", true);
        if (targetId.HasValue && sample.Target != null && sample.Target.Id != targetId)
            Finish("TargetSwitch", false);
        if (sample.Target != null)
        {
            targetId ??= sample.Target.Id;
            lastSeen = sample.Timestamp;
        }
        if (!targetId.HasValue) return;
        if (sample.Target == null && sample.Timestamp - lastSeen > LostTargetGraceSeconds)
        {
            Finish("TargetLost", false);
            return;
        }
        samples.Add(sample);
        if (clickEdge)
        {
            Finish("Click", false);
            waitingForRelease = true;
        }
        else if (sample.Timestamp - samples[0].Timestamp >= MaxDurationSeconds)
            Finish("Timeout", true);
    }

    public void Finish(string reason = "SessionEnd", bool truncated = true)
    {
        if (targetId.HasValue && samples.Count > 0)
            Completed?.Invoke(new Engagement(targetId.Value, samples.ToArray(), reason, truncated));
        samples.Clear();
        targetId = null;
    }
}
