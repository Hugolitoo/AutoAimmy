using System.IO;
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
    public long DroppedEvents => Interlocked.Read(ref dropped);
    public Task Completion => worker;
    public string DirectoryPath { get; }
    public GameplayRecorder(string directory, int capacity = 8192, string appVersion = "development", IReadOnlyDictionary<string, object?>? telemetry = null)
    {
        this.appVersion = appVersion;
        this.telemetry = telemetry;
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
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "analysis.txt"), $"AutoAimmy version: {appVersion}\n" + SessionAnalyzer.Report(profile, DroppedEvents));
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "quality.json"), JsonSerializer.Serialize(new { AppVersion = appVersion, DroppedEvents, Input = "DesktopCursorPollingAndPassiveRawMouse", SampleIntervalMs = 8, TargetStaleAfterMs = 150, Telemetry = telemetry }));
    }
    public async ValueTask DisposeAsync()
    {
        channel.Writer.TryComplete();
        await worker.ConfigureAwait(false);
    }
}
