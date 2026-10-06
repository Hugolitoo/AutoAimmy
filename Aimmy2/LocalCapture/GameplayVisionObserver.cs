using Aimmy2.Adaptive;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Aimmy2.LocalCapture;

internal sealed class GameplayVisionObserver
{
    private readonly ImpactCueDetector detector = new();
    private RectangleF previousTarget;
    private DateTime lastTarget, lastFiring;
    private int stableFrames;
    public long HeadMarkers { get; private set; }
    public long BloodCues { get; private set; }
    public string Detail => $"Indices visuels probables : {HeadMarkers} marqueurs au viseur · {BloodCues} apparitions de sang. À vérifier dans les images ; ce ne sont pas des touches confirmées.";
    public ImpactCue Observe(Bitmap bitmap, Rectangle bounds, IReadOnlyList<LocalDetectionBox> boxes,
        DateTime at, bool firing, PointF reticle, int screenHeight)
    {
        if (firing) lastFiring = at;
        double rx = reticle.X - bounds.X, ry = reticle.Y - bounds.Y;
        var target = boxes.Where(b => new[] { b.X, b.Y, b.Width, b.Height, b.Confidence }.All(double.IsFinite) &&
            b.Confidence >= .65 && b.Width > 0 && b.Height > 0 && b.Width <= bitmap.Width && b.Height <= bitmap.Height &&
            rx >= b.X - 12 && rx <= b.X + b.Width + 12 && ry >= b.Y - 12 && ry <= b.Y + b.Height + 12)
            .OrderByDescending(b => b.Confidence).FirstOrDefault();
        RectangleF region = previousTarget;
        if (target != null)
        {
            region = new((float)target.X, (float)target.Y, (float)target.Width, (float)target.Height);
            var overlap = RectangleF.Intersect(previousTarget, region);
            double union = previousTarget.Width * previousTarget.Height + region.Width * region.Height - overlap.Width * overlap.Height;
            bool same = at - lastTarget <= TimeSpan.FromSeconds(.3) && union > 0 && overlap.Width * overlap.Height / union >= .35;
            if (!same) { stableFrames = 0; detector.Reset(); }
            stableFrames++; previousTarget = region; lastTarget = at;
        }
        bool stable = stableFrames >= 3 && at - lastTarget <= TimeSpan.FromSeconds(.25);
        if (!stable || !bounds.Contains(Point.Round(reticle))) return new();
        float side = Math.Clamp(screenHeight * .07f, 40, 120);
        var marker = ReadRegion(bitmap, new((float)rx - side / 2, (float)ry - side / 2, side, side), 64);
        region.Inflate(region.Width * .15f, region.Height * .1f);
        var blood = ReadRegion(bitmap, region, 64);
        var global = ReadRegion(bitmap, new(0, 0, bitmap.Width, bitmap.Height), 64);
        var cue = detector.Observe((at - DateTime.UnixEpoch).TotalSeconds, ImpactCueDetector.MarkerStrength(marker, 64),
            RedFraction(blood), RedFraction(global), at >= lastFiring && at - lastFiring <= TimeSpan.FromSeconds(.25), stable);
        if (cue.ProbableHeadMarker) HeadMarkers++;
        if (cue.ProbableBlood) BloodCues++;
        return cue;
    }
    private static double RedFraction(byte[] rgb)
    {
        int red = 0;
        for (int i = 0; i < rgb.Length; i += 3)
            if (rgb[i] > 70 && rgb[i] > rgb[i + 1] * 1.6 && rgb[i] > rgb[i + 2] * 1.4 && rgb[i] - rgb[i + 1] > 35) red++;
        return rgb.Length == 0 ? 0 : red / (rgb.Length / 3d);
    }
    private static byte[] ReadRegion(Bitmap source, RectangleF region, int size)
    {
        region = RectangleF.Intersect(region, new(0, 0, source.Width, source.Height));
        var rgb = new byte[size * size * 3];
        if (region.Width < 2 || region.Height < 2) return rgb;
        using var small = new Bitmap(size, size, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(small))
        { graphics.InterpolationMode = InterpolationMode.Bilinear; graphics.DrawImage(source, new RectangleF(0, 0, size, size), region, GraphicsUnit.Pixel); }
        var bits = small.LockBits(new(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[size * 3];
            for (int y = 0; y < size; y++)
            {
                Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), row, 0, row.Length);
                for (int x = 0; x < size; x++)
                { int d = (y * size + x) * 3, s = x * 3; rgb[d] = row[s + 2]; rgb[d + 1] = row[s + 1]; rgb[d + 2] = row[s]; }
            }
        }
        finally { small.UnlockBits(bits); }
        return rgb;
    }
}
