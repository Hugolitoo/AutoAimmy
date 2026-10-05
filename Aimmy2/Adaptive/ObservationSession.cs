using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aimmy2.AILogic;
using Aimmy2.Class;

namespace Aimmy2.Adaptive;

public sealed record ObservationOptions
{
    public bool Enabled { get; init; }
    public bool OfflineTrainerConfirmed { get; init; }
    public int DurationSeconds { get; init; } = 600;
    public int QueueCapacity { get; init; } = 8192;
    public string OutputDirectory { get; init; } = "sessions";
    public AimReference AimReference { get; init; } = AimReference.ScreenCenter;
}

/// <summary>Configuration is latched for the process lifetime. Output remains blocked after recording ends.</summary>
public static class ObservationMode
{
    public static string DataDirectory => Environment.GetEnvironmentVariable("AUTOAIMMY_DATA_DIR") ?? AppContext.BaseDirectory;
    public static string ExportDirectory => Environment.GetEnvironmentVariable("AUTOAIMMY_DATA_DIR") is string data
        ? Path.GetFullPath(Path.Combine(data, "..", "exports")) : Path.Combine(DataDirectory, "exports");
    public static ObservationOptions Options { get; } = Load();
    public static bool BlocksOutput => Options.Enabled;
    private static ObservationOptions Load()
    {
        string path = Path.Combine(DataDirectory, "adaptive.json");
        if (!File.Exists(path)) return new();
        var options = JsonSerializer.Deserialize<ObservationOptions>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } })
            ?? throw new InvalidDataException("adaptive.json is empty.");
        if (options.Enabled && !options.OfflineTrainerConfirmed)
            throw new InvalidDataException("Observation requires OfflineTrainerConfirmed=true.");
        if (!Enum.IsDefined(options.AimReference) || options.DurationSeconds is < 1 or > 3600 || options.QueueCapacity is < 128 or > 65536 || string.IsNullOrWhiteSpace(options.OutputDirectory))
            throw new InvalidDataException("Invalid observation duration, capacity or output directory.");
        return options;
    }
}

/// <summary>Read-only Windows adapter. Independent input sampling; short-lived target identities are approximate.</summary>
internal sealed class ObservationSession : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private readonly CancellationTokenSource stop = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly GameplayRecorder recorder;
    private readonly Task sampler;
    private readonly RawMouseObserver rawMouse = new();
    private TargetObservation? target;
    private TargetObservation? previousTarget;
    private long nextId;
    private int disposed;
    public bool IsRecording => !sampler.IsCompleted;
    public bool HasFailed => sampler.IsFaulted;
    public string? Failure => sampler.Exception?.GetBaseException().Message;
    public double ElapsedSeconds => Math.Min(clock.Elapsed.TotalSeconds, ObservationMode.Options.DurationSeconds);
    public string SessionDirectory => recorder.DirectoryPath;
    public string? ReportPath => recorder.ExportPath;
    public string? ExportFailure => recorder.ExportError;
    public PlayerSessionContext Context { get; }

    public ObservationSession()
    {
        var options = ObservationMode.Options;
        string root = Path.GetFullPath(options.OutputDirectory, ObservationMode.DataDirectory);
        Context = PlayerSessionContext.Load(ObservationMode.DataDirectory);
        recorder = new(Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]), options.QueueCapacity,
            Environment.GetEnvironmentVariable("AUTOAIMMY_VERSION") ?? typeof(ObservationSession).Assembly.GetName().Version?.ToString() ?? "development",
            new Dictionary<string, object?> { ["AimReference"] = options.AimReference.ToString(), ["RawMouseAvailable"] = rawMouse.TryRead(out _, out _, out _), ["RawMouseFailure"] = rawMouse.Failure, ["Calibration"] = "Uncalibrated counts; angular metrics unavailable", ["DetectionValidation"] = "Not confirmed" },
            Context, ObservationMode.ExportDirectory);
        sampler = Task.Run(SampleAsync);
        _ = sampler.ContinueWith(task =>
        {
            var app = System.Windows.Application.Current;
            if (app == null || app.Dispatcher.HasShutdownStarted) return;
            app.Dispatcher.BeginInvoke(new Action(() => global::Other.LogManager.Log(
                task.IsFaulted ? global::Other.LogManager.LogLevel.Error : global::Other.LogManager.LogLevel.Info,
                task.IsFaulted ? $"Observation failed: {task.Exception?.GetBaseException().Message}" :
                    $"Observation complete: {recorder.DirectoryPath}. " +
                    (recorder.ExportPath != null ? $"Report: {recorder.ExportPath}. " : $"Automatic export unavailable: {recorder.ExportError}. Use manual export. ") +
                    "Mouse output remains blocked.", true, 8000)));
        }, TaskScheduler.Default);
    }

    // Called under AIManager's inference gate; target snapshot is atomically published to the sampler.
    public Prediction? UpdateTargets(IReadOnlyList<Prediction> predictions)
    {
        double now = clock.Elapsed.TotalSeconds;
        Prediction? selected = null;
        if (previousTarget != null && now - previousTarget.ObservedAt <= .15)
            selected = predictions.Where(p => p.ClassId == previousTarget.ClassId &&
                Distance(p.ScreenCenterX - previousTarget.X, p.ScreenCenterY - previousTarget.Y) <= Math.Max(30, Math.Max(previousTarget.Width, previousTarget.Height)))
                .MinBy(p => Distance(p.ScreenCenterX - previousTarget.X, p.ScreenCenterY - previousTarget.Y));
        bool retained = selected != null;
        if (selected == null && GetCursorPos(out var cursor))
        {
            var reference = GetReference(cursor);
            selected = predictions.MinBy(p => Distance(p.ScreenCenterX - reference.X, p.ScreenCenterY - reference.Y));
        }
        if (selected == null) { Interlocked.Exchange(ref target, null); return null; }
        var snapshot = new TargetObservation(retained ? previousTarget!.Id : ++nextId,
            selected.ScreenCenterX, selected.ScreenCenterY, selected.Rectangle.Width, selected.Rectangle.Height,
            selected.Confidence, selected.ClassId, now);
        previousTarget = snapshot;
        Interlocked.Exchange(ref target, snapshot);
        return selected;
    }
    private static double Distance(double x, double y) => Math.Sqrt(x * x + y * y);
    private static (double X, double Y) GetReference(CursorPoint cursor) => ObservationMode.Options.AimReference == AimReference.ScreenCenter
        ? (DisplayManager.ScreenLeft + DisplayManager.ScreenWidth / 2.0, DisplayManager.ScreenTop + DisplayManager.ScreenHeight / 2.0)
        : (cursor.X, cursor.Y);

    private async Task SampleAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(8));
            CursorPoint? previous = null;
            long previousRawX = 0, previousRawY = 0;
            bool previousRawAvailable = false;
            double lastMotion = 0, baselineStart = clock.Elapsed.TotalSeconds;
            long sequence = 0;
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
            {
                double now = clock.Elapsed.TotalSeconds;
                if (now >= ObservationMode.Options.DurationSeconds) break;
                if (recorder.Completion.IsFaulted) await recorder.Completion.ConfigureAwait(false);
                if (!GetCursorPos(out var point)) { previous = null; baselineStart = now; previousRawAvailable = false; continue; }
                bool rawAvailable = rawMouse.TryRead(out long rawX, out long rawY, out _);
                double? rawDx = rawAvailable && previousRawAvailable ? rawX - previousRawX : null;
                double? rawDy = rawAvailable && previousRawAvailable ? rawY - previousRawY : null;
                double cursorDx = previous.HasValue ? point.X - previous.Value.X : 0;
                double cursorDy = previous.HasValue ? point.Y - previous.Value.Y : 0;
                bool motionKnown = ObservationMode.Options.AimReference == AimReference.Cursor ? previous.HasValue : rawDx.HasValue;
                double motion = ObservationMode.Options.AimReference == AimReference.Cursor ? Distance(cursorDx, cursorDy) : Distance(rawDx ?? 0, rawDy ?? 0);
                if (motion > 0) lastMotion = now;
                bool baselineValid = motionKnown && now - baselineStart >= .2 && now - lastMotion >= .15;
                var reference = GetReference(point);
                var snapshot = Volatile.Read(ref target);
                if (snapshot != null && now - snapshot.ObservedAt > .15) snapshot = null;
                recorder.TryRecord(new(now, point.X, point.Y, cursorDx, cursorDy,
                    (GetAsyncKeyState(1) & 0x8000) != 0, snapshot, ++sequence, ObservationMode.Options.AimReference,
                    reference.X, reference.Y, rawDx, rawDy, MotionBaselineValid: baselineValid));
                previous = point;
                previousRawX = rawX; previousRawY = rawY; previousRawAvailable = rawAvailable;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { rawMouse.Dispose(); await recorder.DisposeAsync().ConfigureAwait(false); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        try { sampler.GetAwaiter().GetResult(); }
        finally { stop.Dispose(); }
    }
}
