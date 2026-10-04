namespace Aimmy2.Adaptive;

/// <summary>A contiguous encounter with one target, including incomplete encounters.</summary>
public sealed record Engagement(long TargetId, IReadOnlyList<GameplayEvent> Samples,
    string EndReason, bool Truncated);
