using System.IO;
using System.Text.Json;

namespace Aimmy2.Adaptive;

public sealed record DeclaredPlayerSettings
{
    public int Schema { get; init; }
    public string? Id { get; init; }
    public string? Label { get; init; }
    public string? Game { get; init; }
    public string? Weapon { get; init; }
    public double? Dpi { get; init; }
    public double? HorizontalSensitivity { get; init; }
    public double? VerticalSensitivity { get; init; }
    public string? Scope { get; init; }
    public double? AdsSensitivity { get; init; }
    public double? Fov { get; init; }
    public string? AspectRatio { get; init; }
    public string? Resolution { get; init; }
    public string? AdsUsageDeclared { get; init; }
    public string? Source { get; init; }
    public string? ImportStatus { get; init; }
    public string? SettingsFileLastWriteUtc { get; init; }
    public Dictionary<string, double>? AdsSensitivityByScope { get; init; }
    public double? AdsUseSpecific { get; init; }
    public double? AdsGlobalSensitivity { get; init; }
    public double? MouseSensitivityMultiplier { get; init; }
    public double? AdsMouseMultiplier { get; init; }
    public double? AspectRatioSetting { get; init; }
}

/// <summary>Immutable snapshot at recording start. Declared context is never a detected game event.</summary>
public sealed record PlayerSessionContext(string Status, DateTime CapturedUtc, DeclaredPlayerSettings? Settings)
{
    public string SettingsSource => Settings?.Source == "SettingsFile" ? "SettingsFile" : "UserDeclared";
    public string ObservedAdsState => "Unknown";
    public static PlayerSessionContext Load(string dataDirectory)
    {
        var now = DateTime.UtcNow;
        string path = Path.Combine(dataDirectory, "active-profile.json");
        if (!File.Exists(path)) return new("NotConfigured", now, null);
        try
        {
            if (new FileInfo(path).Length > 32768) return new("InvalidProfile", now, null);
            var settings = JsonSerializer.Deserialize<DeclaredPlayerSettings>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            bool InRange(double? value, double min, double max) => value == null || double.IsFinite(value.Value) && value >= min && value <= max;
            if (settings == null || settings.Schema != 1 || !InRange(settings.Dpi, 1, 64000) ||
                !InRange(settings.HorizontalSensitivity, 0, 1000) || !InRange(settings.VerticalSensitivity, 0, 1000) ||
                !InRange(settings.AdsSensitivity, 0, 1000) || !InRange(settings.Fov, 1, 180) ||
                !InRange(settings.AdsUseSpecific, 0, 1) || !InRange(settings.AdsGlobalSensitivity, 0, 1000) ||
                !InRange(settings.MouseSensitivityMultiplier, 0, 10) || !InRange(settings.AdsMouseMultiplier, 0, 10) ||
                !InRange(settings.AspectRatioSetting, 0, 20) ||
                new[] { settings.Id, settings.Label, settings.Game, settings.Weapon, settings.Scope, settings.AspectRatio, settings.Resolution, settings.AdsUsageDeclared, settings.Source, settings.ImportStatus, settings.SettingsFileLastWriteUtc }.Any(s => s?.Length > 120))
                return new("InvalidProfile", now, null);
            var allowedScopes = new[] { "ADSMouseSensitivity1x", "ADSMouseSensitivity1xHalf", "ADSMouseSensitivity2x", "ADSMouseSensitivity2xHalf", "ADSMouseSensitivity3x", "ADSMouseSensitivity4x", "ADSMouseSensitivity5x", "ADSMouseSensitivity8x", "ADSMouseSensitivity12x" };
            if (settings.AdsSensitivityByScope != null)
                settings = settings with { AdsSensitivityByScope = settings.AdsSensitivityByScope
                    .Where(pair => allowedScopes.Contains(pair.Key) && InRange(pair.Value, 0, 1000))
                    .ToDictionary(pair => pair.Key, pair => pair.Value) };
            return new("Configured", now, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new("InvalidProfile", now, null); }
    }
}
