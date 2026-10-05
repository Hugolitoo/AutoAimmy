using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;

namespace Aimmy2.Adaptive;

/// <summary>Bounded nonblocking producers; one worker owns segmentation, analysis and buffered disk IO.</summary>
public sealed class GameplayRecorder : IAsyncDisposable
{
    private readonly Channel<GameplayEvent> channel;
    private readonly Task worker;
    private long dropped;
    private readonly string appVersion;
    private readonly IReadOnlyDictionary<string, object?>? telemetry;
    private readonly PlayerSessionContext? sessionContext;
    private readonly string? reportExportDirectory;
    public string? ExportPath { get; private set; }
    public string? ExportError { get; private set; }
    public long DroppedEvents => Interlocked.Read(ref dropped);
    public Task Completion => worker;
    public string DirectoryPath { get; }
    public GameplayRecorder(string directory, int capacity = 8192, string appVersion = "development", IReadOnlyDictionary<string, object?>? telemetry = null, PlayerSessionContext? sessionContext = null, string? reportExportDirectory = null)
    {
        this.appVersion = appVersion;
        this.telemetry = telemetry;
        this.sessionContext = sessionContext;
        this.reportExportDirectory = reportExportDirectory;
        DirectoryPath = directory;
        channel = Channel.CreateBounded<GameplayEvent>(new BoundedChannelOptions(capacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        worker = Task.Run(ConsumeAsync);
    }
    public bool TryRecord(GameplayEvent value)
    {
        if (worker.IsFaulted || !channel.Writer.TryWrite(value))
        { Interlocked.Increment(ref dropped); return false; }
        return true;
    }
    private async Task ConsumeAsync()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (sessionContext != null)
            await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "context.json"), JsonSerializer.Serialize(sessionContext, new JsonSerializerOptions { WriteIndented = true }));
        using var events = new StreamWriter(Path.Combine(DirectoryPath, "events.jsonl"), false, System.Text.Encoding.UTF8, 65536);
        using var engagements = new StreamWriter(Path.Combine(DirectoryPath, "engagements.jsonl"), false, System.Text.Encoding.UTF8, 65536);
        var segmenter = new EngagementSegmenter();
        var extractor = new FeatureExtractor();
        var analyzer = new SessionAnalyzer();
        segmenter.Completed += e =>
        {
            var f = extractor.Extract(e);
            analyzer.Add(f);
            engagements.WriteLine(JsonSerializer.Serialize(f));
        };
        await foreach (var value in channel.Reader.ReadAllAsync())
        {
            await events.WriteLineAsync(JsonSerializer.Serialize(value));
            segmenter.Push(value);
        }
        segmenter.Finish();
        var profile = analyzer.Analyze();
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "analysis.json"), JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        string contextText = sessionContext == null ? "" : $"Declared player profile: {sessionContext.Settings?.Label ?? sessionContext.Status}; weapon: {sessionContext.Settings?.Weapon ?? "unknown"}; scope: {sessionContext.Settings?.Scope ?? "unknown"}. Actual ADS state is unobserved. See context.json.\n";
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "analysis.txt"), $"AutoAimmy version: {appVersion}\n" + contextText + SessionAnalyzer.Report(profile, DroppedEvents));
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "quality.json"), JsonSerializer.Serialize(new { AppVersion = appVersion, DroppedEvents, Input = "DesktopCursorPollingAndPassiveRawMouse", SampleIntervalMs = 8, TargetStaleAfterMs = 150, Telemetry = telemetry }));
        // Complete buffered files before creating the local, whitelisted report.
        events.Close();
        engagements.Close();
        if (reportExportDirectory != null)
        {
            string? temporary = null;
            try
            {
                Directory.CreateDirectory(reportExportDirectory);
                string name = "AutoAimmy-report-" + new DirectoryInfo(DirectoryPath).Name;
                string destination = Path.Combine(reportExportDirectory, name + ".zip");
                if (File.Exists(destination)) destination = Path.Combine(reportExportDirectory, name + "-" + Guid.NewGuid().ToString("N") + ".zip");
                temporary = Path.Combine(reportExportDirectory, Guid.NewGuid().ToString("N") + ".tmp");
                using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
                    foreach (string file in new[] { "analysis.json", "analysis.txt", "quality.json", "engagements.jsonl", "context.json" })
                    {
                        string source = Path.Combine(DirectoryPath, file);
                        if (File.Exists(source)) archive.CreateEntryFromFile(source, file);
                    }
                File.Move(temporary, destination);
                ExportPath = destination;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Export failure must not invalidate the recorded session; manual export remains available.
                ExportError = error.Message;
            }
            finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        channel.Writer.TryComplete();
        await worker.ConfigureAwait(false);
    }
}
