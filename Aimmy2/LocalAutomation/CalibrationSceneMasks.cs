using System.Drawing;

namespace Aimmy2.LocalAutomation;

internal static class CalibrationSceneMasks
{
    // Exclude optic/weapon, reticle and HUD. This is not scope recognition;
    // views without enough uncovered texture remain uncalibrated.
    public static RectangleF[] ForBounds(Rectangle bounds)
    {
        RectangleF Region(float x, float y, float width, float height) => new(bounds.X + bounds.Width * x,
            bounds.Y + bounds.Height * y, bounds.Width * width, bounds.Height * height);
        return new[] { Region(.25f, .20f, .50f, .80f), Region(0, 0, .30f, .32f),
            Region(.70f, 0, .30f, .20f), Region(0, .72f, 1, .28f), Region(.43f, 0, .14f, .18f) };
    }
}
