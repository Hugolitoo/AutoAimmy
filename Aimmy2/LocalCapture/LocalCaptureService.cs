using Aimmy2.Adaptive;
using Aimmy2.Class;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Aimmy2.LocalCapture;

/// <summary>Explicitly started local capture. Never writes frames while another application is foreground.</summary>
public sealed class LocalCaptureService
{
    private sealed record DetectionSnapshot(DateTime CapturedUtc, Rectangle Bounds, LocalDetectionBox[] Boxes, string? ModelName);
    public static LocalCaptureService Instance { get; } = new();
    private readonly object gate = new();
    private LocalCaptureRecorder? recorder;
    private CancellationTokenSource? stop;
    private Task? worker;
    private LocalCaptureOptions options = new();
    private DetectionSnapshot? latestDetections;
    private R6ForegroundSnapshot? foreground;
    private string captureStatus = "Stopped";
    private string? captureError;
    private long nextDetectionSample;

    public LocalCaptureState State
    {
        get
        {
            var current = Volatile.Read(ref recorder)?.State ?? new();
            return current.Active ? current with { Status = Volatile.Read(ref captureStatus), Error = Volatile.Read(ref captureError) } : current;
        }
    }

    public void Start(string dataDirectory, PlayerSessionContext context, LocalCaptureOptions? requestedOptions = null)
    {
        lock (gate)
        {
            if (worker is { IsCompleted: false }) return;
            options = requestedOptions ?? new();
            options.Validate();
            recorder = new(dataDirectory, context, options);
            stop?.Dispose();
            stop = new();
            latestDetections = null; foreground = null; nextDetectionSample = 0;
            captureStatus = "WaitingForR6"; captureError = null;
            var token = stop.Token;
            var activeRecorder = recorder;
            worker = Task.Run(() => CaptureAsync(activeRecorder, token));
        }
    }

    public bool PublishInput(LocalInputSample sample)
    {
        var window = Volatile.Read(ref foreground);
        return window != null && R6ForegroundGuard.IsSameForegroundWindow(window) &&
            (Volatile.Read(ref recorder)?.TryRecordInput(sample) ?? false);
    }

    public bool PublishDetections(Bitmap bitmap, Rectangle captureBounds, IReadOnlyList<LocalDetectionBox> detections,
        DateTime capturedUtc, string? modelName, R6ForegroundSnapshot capturedForeground)
    {
        var active = Volatile.Read(ref recorder);
        if (active == null || !active.State.Active || !capturedForeground.ClientBounds.Contains(captureBounds) ||
            !R6ForegroundGuard.StillMatches(capturedForeground)) return false;
        Volatile.Write(ref latestDetections, new(capturedUtc, captureBounds, detections.Take(512).ToArray(), modelName));
        long now = Environment.TickCount64;
        lock (gate)
        {
            if (now < nextDetectionSample) return false;
            nextDetectionSample = now + options.DetectionSampleIntervalMilliseconds;
        }
        try { return active.TryRecord(bitmap, capturedUtc, "DetectionCrop", captureBounds, detections, modelName,
            capturedUtc, captureBounds); }
        catch (Exception error)
        {
            Volatile.Write(ref captureError, "Image candidate non enregistrée : " + error.Message);
            return false;
        }
    }

    private async Task CaptureAsync(LocalCaptureRecorder active, CancellationToken token)
    {
        try
        {
            using var capture = new global::AILogic.CaptureManager();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / options.FramesPerSecond));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false) && active.State.Active)
            {
                if (!R6ForegroundGuard.TryGet(out var window))
                {
                    Volatile.Write(ref foreground, null);
                    Volatile.Write(ref captureStatus, "WaitingForR6");
                    continue;
                }
                try
                {
                    var monitor = new Rectangle(DisplayManager.ScreenLeft, DisplayManager.ScreenTop, DisplayManager.ScreenWidth, DisplayManager.ScreenHeight);
                    var bounds = Rectangle.Intersect(window.ClientBounds, monitor);
                    if (bounds.Width < 320 || bounds.Height < 200)
                    {
                        Volatile.Write(ref foreground, null);
                        Volatile.Write(ref captureStatus, "WrongMonitor");
                        continue;
                    }
                    DateTime capturedUtc = DateTime.UtcNow;
                    using var frame = capture.ScreenGrab(bounds, requireFresh: true);
                    if (frame == null || !R6ForegroundGuard.StillMatches(window))
                    {
                        Volatile.Write(ref foreground, null);
                        Volatile.Write(ref captureStatus, frame == null ? "CaptureUnavailable" : "WaitingForR6");
                        continue;
                    }
                    Volatile.Write(ref foreground, window);
                    double factor = Math.Min(1, (double)options.MaximumFullFrameWidth / frame.Width);
                    using var resized = new Bitmap(Math.Max(1, (int)(frame.Width * factor)), Math.Max(1, (int)(frame.Height * factor)));
                    using (var graphics = Graphics.FromImage(resized))
                    {
                        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        graphics.DrawImage(frame, 0, 0, resized.Width, resized.Height);
                    }
                    if (!R6ForegroundGuard.StillMatches(window)) { Volatile.Write(ref foreground, null); continue; }
                    var detections = Volatile.Read(ref latestDetections);
                    if (detections != null && Math.Abs((capturedUtc - detections.CapturedUtc).TotalMilliseconds) > 500) detections = null;
                    active.TryRecord(resized, capturedUtc, "FullGame", bounds, detections?.Boxes ?? Array.Empty<LocalDetectionBox>(),
                        detections?.ModelName, detections?.CapturedUtc, detections?.Bounds);
                    Volatile.Write(ref captureStatus, "Recording");
                    Volatile.Write(ref captureError, null);
                }
                catch (Exception error)
                {
                    Volatile.Write(ref foreground, null);
                    Volatile.Write(ref captureStatus, "CaptureUnavailable");
                    Volatile.Write(ref captureError, error.GetType().Name + ": " + error.Message);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { Volatile.Write(ref foreground, null); await active.DisposeAsync().ConfigureAwait(false); }
    }

    public async Task StopAsync()
    {
        Task? pending;
        lock (gate) { stop?.Cancel(); pending = worker; }
        if (pending != null) await pending.ConfigureAwait(false);
    }
}
