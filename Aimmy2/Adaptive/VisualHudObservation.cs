using System.Text.RegularExpressions;

namespace Aimmy2.Adaptive;

public sealed record VisualHudObservation(DateTime ReadUtc, string Status, string? WeaponText, string? ScopeText,
    string AdsState = "Unknown", string Source = "LocalWindowsOCR", string? Detail = null, DateTime? FrameUtc = null);

/// <summary>Recognizes explicit HUD text only. Icons, ADS and hardware DPI are never inferred from mouse input.</summary>
public static class HudTextParser
{
    private static readonly string[] Weapons = { "M4", "AR33", "G36C", "416-C", "L85A2", "R4-C", "AK-12", "MP5", "MP5K", "MP7", "P90", "C8-SFW", "552 COMMANDO", "556XI", "SC3000K", "FMG-9", "F2", "T-5", "9X19VSN", "M762", "AK-74M", "AUG A2", "SPEAR .308", "MK17 CQB" };
    public static (string? Weapon, string? Scope) Parse(string text)
    {
        if (text.Length > 12000) return (null, null);
        var matches = Weapons.Where(w => Regex.IsMatch(text, @"(?<![A-Z0-9])" + Regex.Escape(w).Replace("\\ ", @"\s+") + @"(?![A-Z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
        var scopes = Regex.Matches(text, @"(?<![0-9])(?:1[.,]0|1[.,]5|2[.,]5|3[.,]5|8[.,]0)\s*[x×](?![A-Z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(m => Regex.Replace(m.Value.ToLowerInvariant().Replace(',', '.').Replace('×', 'x'), @"\s+", "")).Distinct().ToArray();
        return (matches.Length == 1 ? matches[0] : null, scopes.Length == 1 ? scopes[0] : null);
    }
}
