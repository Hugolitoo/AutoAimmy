using System.IO;
using System.Text.Json;

namespace Aimmy2.AdaptiveControl;

/// <summary>Small local-only checkpoints. No screen frames, raw input history, paths or network calls.</summary>
public static class AdaptiveProfileStore
{
    private const int MaximumBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> Keys = Enum.GetValues<ApparentSize>()
        .SelectMany(size => Enum.GetValues<ScreenMotion>().Select(motion => $"{size}/{motion}")).ToHashSet(StringComparer.Ordinal);

    public static AdaptivePlayerState Load(string path) => Load(path, out _);

    public static AdaptivePlayerState Load(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path)) return new();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("Adaptive profile exceeds 64 KiB.");
            var state = JsonSerializer.Deserialize<AdaptivePlayerState>(stream, JsonOptions) ?? throw new InvalidDataException("Empty adaptive profile.");
            if (state.Schema != 1) throw new InvalidDataException("Unsupported adaptive profile schema.");
            return Sanitize(state);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { error = ex.Message; return new(); }
    }

    public static void Save(string path, AdaptivePlayerState state)
    {
        var clean = Sanitize(state);
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(clean, JsonOptions);
        if (content.Length > MaximumBytes) throw new InvalidDataException("Adaptive profile exceeds 64 KiB.");
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(content); stream.Flush(true); }
            File.Move(temporary, fullPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static AdaptivePlayerState Sanitize(AdaptivePlayerState state)
    {
        if (state.Schema != 1) return new();
        double Bounded(double value, double min, double max, double fallback = 0) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
        return new()
        {
            PlayerKey = !string.IsNullOrWhiteSpace(state.PlayerKey) && state.PlayerKey.Length <= 120 &&
                state.PlayerKey.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.') ? state.PlayerKey : "local",
            Calibration = state.Calibration?.IsUsable == true ? state.Calibration : null,
            Profiles = (state.Profiles ?? Array.Empty<ContextProfile>()).Where(p => p != null && p.Key != null &&
                (Keys.Contains(p.Key) || System.Text.RegularExpressions.Regex.IsMatch(p.Key, @"^Adaptive/[0-9]{2}$")))
                .GroupBy(p => p.Key, StringComparer.Ordinal).Select(g => g.Last()).Take(36).Select(p => p with
                {
                    Gain = Bounded(p.Gain, .06, .22, .15),
                    SmoothingSeconds = Bounded(p.SmoothingSeconds, .025, .14, .07),
                    ObservationFrames = Math.Clamp(p.ObservationFrames, 0, 1_000_000_000),
                    ObservedSeconds = Bounded(p.ObservedSeconds, 0, 1_000_000_000),
                    MeanErrorInTargetRadii = Bounded(p.MeanErrorInTargetRadii, 0, 50),
                    MeanScreenSpeedInHeightsPerSecond = Bounded(p.MeanScreenSpeedInHeightsPerSecond, 0, 20),
                    AssistedFrames = Math.Clamp(p.AssistedFrames, 0, 1_000_000_000),
                    ErrorSignCrossings = Math.Clamp(p.ErrorSignCrossings, 0, 1_000_000_000),
                    SizeFeature = Bounded(p.SizeFeature, 0, 2),
                    SpeedFeature = Bounded(p.SpeedFeature, 0, 20),
                    HorizontalFeature = Bounded(p.HorizontalFeature, 0, 1),
                    FeatureSamples = Math.Clamp(p.FeatureSamples, 0, 1_000_000_000),
                    ComparedWindows = Math.Clamp(p.ComparedWindows, 0, 1_000_000_000),
                    AdjustmentEvidence = p.AdjustmentEvidence is "RepeatedScreenErrorCrossingsDuringAssistance_CausalityUnverified" or "MeasuredRandomizedTrackingWindows" ?
                        p.AdjustmentEvidence : "ConservativeDefault"
                }).ToArray()
        };
    }
}
