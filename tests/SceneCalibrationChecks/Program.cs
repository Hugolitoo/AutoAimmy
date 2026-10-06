using Aimmy2.AdaptiveControl;
using Aimmy2.LocalAutomation;
using System.Drawing;
using System.Drawing.Imaging;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
int Pixel(int x, int y) { uint value = unchecked((uint)(x * 73856093 ^ y * 19349663)); value ^= value >> 13; return (int)(value % 220) + 20; }
Bitmap Frame(int offsetX, int offsetY, bool flat = false)
{
    var image = new Bitmap(160, 120, PixelFormat.Format32bppArgb);
    for (int y = 0; y < 120; y++) for (int x = 0; x < 160; x++)
    {
        bool optic = x >= 40 && x < 120 && y >= 24;
        int value = optic ? Pixel(x + 900, y + 900) : flat ? 30 : Pixel(x + offsetX, y + offsetY);
        image.SetPixel(x, y, Color.FromArgb(value, value, value));
    }
    return image;
}
var bounds = new Rectangle(-960, 100, 960, 540);
var observer = new SceneMotionObserver(320, 180);
var masks = CalibrationSceneMasks.ForBounds(bounds);
using (var first = Frame(0, 0)) observer.Observe(first, bounds, [], 0, masks);
using (var second = Frame(1, 0))
{
    var motion = observer.Observe(second, bounds, [], .1, masks);
    Check(motion.Reliable && Math.Abs(motion.X + 6) < .2 && Math.Abs(motion.Y) < .2,
        "wide scene registration follows moving decor despite a large fixed optic and offset monitor");
}
var calibration = new GuidedCalibration("masked-scene", 1080);
observer.Reset(); double px = 1000, py = 1000, time = 0; int sx = 0, sy = 0;
for (int axis = 0; axis < 2; axis++)
for (int i = 0; i < 80; i++)
{
    int delta = i % 16 < 8 ? 1 : -1;
    if (axis == 0) sx += delta; else sy += delta;
    using var image = Frame(sx, sy);
    var motion = observer.Observe(image, bounds, [], time, masks);
    if (motion.Reliable)
    {
        px += motion.X; py += motion.Y;
        calibration.Observe(new(time, 1, px, py, axis == 0 ? delta * 30 : 0,
            axis == 1 ? delta * 22.5 : 0, true, false, 100));
    }
    else calibration.BreakInterval();
    time += .1;
}
var result = calibration.Evaluate();
Check(result.IsUsable && Math.Abs(result.PixelsPerCountX + .2) < .01 && Math.Abs(result.PixelsPerCountY + .2) < .01,
    "masked wide camera measurements calibrate both axes without using fixed weapon pixels");
observer.Reset();
using (var first = Frame(0, 0, true)) observer.Observe(first, bounds, [], 0, masks);
using (var second = Frame(1, 1, true))
    Check(!observer.Observe(second, bounds, [], .1, masks).Reliable,
        "a textured fixed optic with blank uncovered decor cannot claim a camera measurement");
Console.WriteLine($"{checks} scene calibration checks passed. Synthetic pixels only; no desktop capture or mouse output.");
