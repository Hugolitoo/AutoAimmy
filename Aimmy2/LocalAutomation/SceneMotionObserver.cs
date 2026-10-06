using Aimmy2.AdaptiveControl;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Aimmy2.LocalAutomation;

internal sealed class SceneMotionObserver
{
    private readonly int width, height;
    public SceneMotionObserver(int width = 160, int height = 120)
    {
        if (width is < 48 or > 320 || height is < 48 or > 240) throw new ArgumentOutOfRangeException(nameof(width));
        this.width = width; this.height = height;
    }
    private byte[]? previous;
    private double previousTime;
    private Rectangle previousBounds;
    private (double X, double Y, double Width, double Height)[] previousMasks = [];
    public SceneMotion Latest { get; private set; } = SceneMotion.Unknown;
    public double Interval { get; private set; }
    public bool Updated { get; private set; }
    public bool Blank { get; private set; }
    public void Reset() { previous = null; Latest = SceneMotion.Unknown; previousMasks = []; }
    public SceneMotion Observe(Bitmap bitmap, Rectangle bounds, IReadOnlyList<DetectionSample> detections, double time,
        IReadOnlyList<RectangleF>? additionalScreenExclusions = null)
    {
        Updated = false;
        if (previous != null && time - previousTime < .07) return time - previousTime <= .15 ? Latest : SceneMotion.Unknown;
        using var small = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(small)) graphics.DrawImage(bitmap, 0, 0, width, height);
        var locked = small.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] pixels = new byte[width * height];
        try
        {
            byte[] row = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(locked.Scan0 + y * locked.Stride, row, 0, row.Length);
                for (int x = 0; x < width; x++) pixels[y * width + x] = (byte)((row[x * 4] * 29 + row[x * 4 + 1] * 150 + row[x * 4 + 2] * 77) >> 8);
            }
        }
        finally { small.UnlockBits(locked); }
        Blank = pixels.Count(value => value < 3) > pixels.Length * .98;
        var masks = detections.Where(d => d.Confidence >= .45).Take(32).Select(d => ((d.X - d.Width / 2 - bounds.X) * width / bounds.Width,
            (d.Y - d.Height / 2 - bounds.Y) * height / bounds.Height, d.Width * width / bounds.Width, d.Height * height / bounds.Height))
            .Concat((additionalScreenExclusions ?? Array.Empty<RectangleF>()).Take(16).Select(r =>
                ((double)(r.X - bounds.X) * width / bounds.Width, (double)(r.Y - bounds.Y) * height / bounds.Height,
                    (double)r.Width * width / bounds.Width, (double)r.Height * height / bounds.Height))).ToArray();
        Interval = time - previousTime;
        Latest = previous != null && previousBounds == bounds && Interval is > 0 and <= GuidedCalibration.MaximumObservationGapSeconds
            ? SceneMotionEstimator.Estimate(previous, pixels, width, height, previousMasks.Concat(masks).ToArray()) : SceneMotion.Unknown;
        Latest = Latest with { X = Latest.X * bounds.Width / width, Y = Latest.Y * bounds.Height / height };
        previous = pixels; previousTime = time; previousBounds = bounds; previousMasks = masks; Updated = true;
        return Latest;
    }
}
