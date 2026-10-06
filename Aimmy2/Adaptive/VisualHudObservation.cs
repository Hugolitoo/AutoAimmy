using System.Text.RegularExpressions;

namespace Aimmy2.Adaptive;

public sealed record VisualHudObservation(DateTime ReadUtc, string Status, string? WeaponText, string? ScopeText,
    string AdsState = "Unknown", string Source = "LocalWindowsOCR", string? Detail = null, DateTime? FrameUtc = null,
    int? AmmoMagazine = null, int? AmmoReserve = null);

public sealed record HudWord(string Text, double X, double Y, double Width, double Height);
public sealed record HudReadout(string Text, HudWord[] Words);

public static class AmmoHudParser
{
    public static (int? Magazine, int? Reserve) Parse(HudReadout readout)
    {
        var explicitPairs = Regex.Matches(readout.Text, @"(?<![0-9+])(\d{1,2})\s*/\s*(\d{1,3})(?![0-9])")
            .Select(m => (Magazine: int.Parse(m.Groups[1].Value), Reserve: int.Parse(m.Groups[2].Value)))
            .Where(p => p.Magazine <= 65 && p.Reserve >= 20 && p.Reserve >= p.Magazine).Distinct().ToArray();
        if (explicitPairs.Length == 1) return explicitPairs[0];
        var numbers = readout.Words.Where(w => w.Text.Length <= 3 && Regex.IsMatch(w.Text, @"^\d{1,3}$") &&
            w.Height >= 10 && new[] { w.X, w.Y, w.Width, w.Height }.All(double.IsFinite)).Take(50).ToArray();
        var pairs = new List<(int Magazine, int Reserve, double Score)>();
        foreach (var magazine in numbers)
        foreach (var reserve in numbers)
        {
            int mag = int.Parse(magazine.Text), res = int.Parse(reserve.Text);
            if (ReferenceEquals(magazine, reserve) || mag > 65 || res < 20 || res < mag || magazine.Height < reserve.Height * 1.25) continue;
            double dx = Math.Abs(magazine.X + magazine.Width / 2 - reserve.X - reserve.Width / 2);
            double dy = Math.Abs(magazine.Y + magazine.Height / 2 - reserve.Y - reserve.Height / 2);
            if (dx > magazine.Height * 4 || dy > magazine.Height * 3) continue;
            pairs.Add((mag, res, magazine.Height - .05 * (dx + dy)));
        }
        var ranked = pairs.OrderByDescending(p => p.Score).ToArray();
        if (ranked.Length == 0 || ranked.Length > 1 && ranked[0].Score - ranked[1].Score < 4) return (null, null);
        return (ranked[0].Magazine, ranked[0].Reserve);
    }
}

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
