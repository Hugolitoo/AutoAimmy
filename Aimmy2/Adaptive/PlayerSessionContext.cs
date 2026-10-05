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
}

/// <summary>Immutable snapshot at recording start. Declared context is never a detected game event.</summary>
public sealed record PlayerSessionContext(string Status, DateTime CapturedUtc, DeclaredPlayerSettings? Settings)
{
    public string SettingsSource => "UserDeclared";
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
                new[] { settings.Id, settings.Label, settings.Game, settings.Weapon, settings.Scope, settings.AspectRatio, settings.Resolution, settings.AdsUsageDeclared }.Any(s => s?.Length > 120))
                return new("InvalidProfile", now, null);
            return new("Configured", now, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new("InvalidProfile", now, null); }
    }
}
