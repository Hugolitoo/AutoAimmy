namespace Aimmy2.Adaptive;

/// <summary>Immutable observation. Time is monotonic seconds; positions use desktop pixels.</summary>
public sealed record TargetObservation(long Id, double X, double Y, double Width,
    double Height, double Confidence, int ClassId, double ObservedAt);

public enum AimReference { Cursor, ScreenCenter }

public sealed record GameplayEvent(double Timestamp, double CursorX, double CursorY,
    double MouseDeltaX, double MouseDeltaY, bool LeftClick, TargetObservation? Target,
    long Sequence = 0, AimReference AimReference = AimReference.Cursor,
    double? AimX = null, double? AimY = null, double? RawMouseDeltaX = null,
    double? RawMouseDeltaY = null, bool? MotionBaselineValid = null)
{
    public double ReferenceX => AimX ?? CursorX;
    public double ReferenceY => AimY ?? CursorY;
}
