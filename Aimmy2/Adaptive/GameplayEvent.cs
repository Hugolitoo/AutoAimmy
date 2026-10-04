namespace Aimmy2.Adaptive;

/// <summary>Immutable observation. Time is monotonic seconds; positions use desktop pixels.</summary>
public sealed record TargetObservation(long Id, double X, double Y, double Width,
    double Height, double Confidence, int ClassId, double ObservedAt);

public sealed record GameplayEvent(double Timestamp, double CursorX, double CursorY,
    double MouseDeltaX, double MouseDeltaY, bool LeftClick, TargetObservation? Target,
    long Sequence = 0);
