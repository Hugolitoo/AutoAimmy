using System.Drawing;

namespace Aimmy2.LocalCapture;

public sealed record LocalCaptureOptions
{
    public int FramesPerSecond { get; init; } = 5;
    public int MaximumFullFrameWidth { get; init; } = 1280;
    public int DetectionSampleIntervalMilliseconds { get; init; } = 1000;
    public int QueueCapacity { get; init; } = 6;
    public long MaximumSessionBytes { get; init; } = 512L * 1024 * 1024;
    public long MaximumTotalBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MinimumFreeBytes { get; init; } = 512L * 1024 * 1024;
    public int MaximumSessionMinutes { get; init; } = 30;
    public void Validate()
    {
        if (FramesPerSecond is < 1 or > 10 || MaximumFullFrameWidth is < 320 or > 1920 ||
            DetectionSampleIntervalMilliseconds is < 100 or > 60000 || QueueCapacity is < 1 or > 16 ||
            MaximumSessionBytes < 65536 || MaximumTotalBytes < MaximumSessionBytes || MinimumFreeBytes < 0 ||
            MaximumSessionMinutes is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(LocalCaptureOptions), "Invalid local recording limits.");
    }
}

public sealed record LocalDetectionBox(double X, double Y, double Width, double Height, double Confidence, int ClassId);
public sealed record LocalVisualCue(bool ProbableHeadMarker, bool ProbableBlood, double MarkerStrength, double BloodIncrease,
    string Evidence = "UnverifiedVisualCue");
public sealed record LocalInputSample(DateTime CapturedUtc, double? RawDeltaX, double? RawDeltaY,
    bool LeftPressed, bool? RightPressed = null, bool AssistanceEnabled = false,
    double? SceneDeltaX = null, double? SceneDeltaY = null, double? SceneConfidence = null,
    double? SceneScale = null, double? SceneIntervalSeconds = null,
    int GeneratedCountsX = 0, int GeneratedCountsY = 0, DateTime? GeneratedUtc = null);
public sealed record LocalCaptureBounds(int X, int Y, int Width, int Height)
{
    public static LocalCaptureBounds From(Rectangle rectangle) => new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
}
public sealed record LocalCaptureFrame(long Sequence, DateTime CapturedUtc, string Kind, string Image,
    int Width, int Height, LocalCaptureBounds CaptureBounds, IReadOnlyList<LocalDetectionBox> Detections,
    string LabelStatus, string SelectionReason, string? ModelName, LocalInputSample? LatestInput,
    DateTime? DetectionsCapturedUtc = null, LocalCaptureBounds? DetectionBounds = null,
    double? DetectionTimeOffsetMilliseconds = null, LocalVisualCue? VisualCue = null);
public sealed record LocalCaptureState(bool Active = false, string Status = "Stopped", string? Directory = null,
    long FullFrames = 0, long CandidateFrames = 0, long ConfidentCandidates = 0, long HardCandidates = 0,
    long DroppedFrames = 0, long InputSamples = 0, long DroppedInputSamples = 0, long BytesWritten = 0,
    string? Error = null, string? PlaybackPath = null);
