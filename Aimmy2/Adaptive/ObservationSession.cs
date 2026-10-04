using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aimmy2.AILogic;

namespace Aimmy2.Adaptive;

public sealed record ObservationOptions
{
    public bool Enabled { get; init; }
    public bool OfflineTrainerConfirmed { get; init; }
    public int DurationSeconds { get; init; } = 600;
    public int QueueCapacity { get; init; } = 8192;
    public string OutputDirectory { get; init; } = "sessions";
}

/// <summary>Configuration is latched for the process lifetime. Output remains blocked after recording ends.</summary>
public static class ObservationMode
{
    public static string DataDirectory => Environment.GetEnvironmentVariable("AUTOAIMMY_DATA_DIR") ?? AppContext.BaseDirectory;
    public static ObservationOptions Options { get; } = Load();
    public static bool BlocksOutput => Options.Enabled;
    private static ObservationOptions Load()
    {
        string path = Path.Combine(DataDirectory, "adaptive.json");
        if (!File.Exists(path)) return new();
        var options = JsonSerializer.Deserialize<ObservationOptions>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("adaptive.json is empty.");
        if (options.Enabled && !options.OfflineTrainerConfirmed)
            throw new InvalidDataException("Observation requires OfflineTrainerConfirmed=true.");
        if (options.DurationSeconds is < 1 or > 3600 || options.QueueCapacity is < 128 or > 65536 || string.IsNullOrWhiteSpace(options.OutputDirectory))
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
    private TargetObservation? target;
    private TargetObservation? previousTarget;
    private long nextId;
    private int disposed;
    public bool IsRecording => !sampler.IsCompleted;

    public ObservationSession()
    {
        var options = ObservationMode.Options;
        string root = Path.GetFullPath(options.OutputDirectory, ObservationMode.DataDirectory);
        recorder = new(Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]), options.QueueCapacity,
            Environment.GetEnvironmentVariable("AUTOAIMMY_VERSION") ?? typeof(ObservationSession).Assembly.GetName().Version?.ToString() ?? "development");
        sampler = Task.Run(SampleAsync);
        _ = sampler.ContinueWith(task =>
        {
            var app = System.Windows.Application.Current;
            if (app == null || app.Dispatcher.HasShutdownStarted) return;
            app.Dispatcher.BeginInvoke(new Action(() => global::Other.LogManager.Log(
                task.IsFaulted ? global::Other.LogManager.LogLevel.Error : global::Other.LogManager.LogLevel.Info,
                task.IsFaulted ? $"Observation failed: {task.Exception?.GetBaseException().Message}" :
                    $"Observation complete: {recorder.DirectoryPath}. Mouse output remains blocked.", true, 8000)));
        }, TaskScheduler.Default);
    }

    // Called under AIManager's inference gate; target snapshot is atomically published to the sampler.
    public void UpdateTargets(IReadOnlyList<Prediction> predictions)
    {
        double now = clock.Elapsed.TotalSeconds;
        Prediction? selected = null;
        if (previousTarget != null && now - previousTarget.ObservedAt <= .15)
            selected = predictions.Where(p => p.ClassId == previousTarget.ClassId &&
                Distance(p.ScreenCenterX - previousTarget.X, p.ScreenCenterY - previousTarget.Y) <= Math.Max(30, Math.Max(previousTarget.Width, previousTarget.Height)))
                .MinBy(p => Distance(p.ScreenCenterX - previousTarget.X, p.ScreenCenterY - previousTarget.Y));
        bool retained = selected != null;
        if (selected == null && GetCursorPos(out var cursor))
            selected = predictions.MinBy(p => Distance(p.ScreenCenterX - cursor.X, p.ScreenCenterY - cursor.Y));
        if (selected == null) { Interlocked.Exchange(ref target, null); return; }
        var snapshot = new TargetObservation(retained ? previousTarget!.Id : ++nextId,
            selected.ScreenCenterX, selected.ScreenCenterY, selected.Rectangle.Width, selected.Rectangle.Height,
            selected.Confidence, selected.ClassId, now);
        previousTarget = snapshot;
        Interlocked.Exchange(ref target, snapshot);
    }
    private static double Distance(double x, double y) => Math.Sqrt(x * x + y * y);

    private async Task SampleAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(8));
            CursorPoint? previous = null;
            long sequence = 0;
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
            {
                double now = clock.Elapsed.TotalSeconds;
                if (now >= ObservationMode.Options.DurationSeconds) break;
                if (recorder.Completion.IsFaulted) await recorder.Completion.ConfigureAwait(false);
                if (!GetCursorPos(out var point)) { previous = null; continue; }
                var snapshot = Volatile.Read(ref target);
                if (snapshot != null && now - snapshot.ObservedAt > .15) snapshot = null;
                recorder.TryRecord(new(now, point.X, point.Y, previous.HasValue ? point.X - previous.Value.X : 0,
                    previous.HasValue ? point.Y - previous.Value.Y : 0, (GetAsyncKeyState(1) & 0x8000) != 0, snapshot, ++sequence));
                previous = point;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { await recorder.DisposeAsync().ConfigureAwait(false); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        try { sampler.GetAwaiter().GetResult(); }
        finally { stop.Dispose(); }
    }
}
