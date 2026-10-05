using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Aimmy2.LocalCapture;

public sealed record R6ForegroundSnapshot(nint Window, uint ProcessId, Rectangle ClientBounds);

/// <summary>Checks only process identity and client coordinates; does not read game memory.</summary>
public static class R6ForegroundGuard
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint id);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);

    public static bool TryGet(out R6ForegroundSnapshot snapshot)
    {
        snapshot = new(0, 0, Rectangle.Empty);
        var window = GetForegroundWindow();
        if (window == 0 || IsIconic(window)) return false;
        GetWindowThreadProcessId(window, out uint id);
        try
        {
            using var process = Process.GetProcessById((int)id);
            var name = process.ProcessName;
            if (!(name.Equals("RainbowSix", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("RainbowSix_Vulkan", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("RainbowSix_DX11", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("RainbowSix_DX12", StringComparison.OrdinalIgnoreCase))) return false;
            if (!GetClientRect(window, out var rect)) return false;
            var point = new NativePoint { X = rect.Left, Y = rect.Top };
            if (!ClientToScreen(window, ref point)) return false;
            var bounds = new Rectangle(point.X, point.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
            if (bounds.Width < 320 || bounds.Height < 200) return false;
            snapshot = new(window, id, bounds);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    public static bool StillMatches(R6ForegroundSnapshot snapshot) => TryGet(out var current) && current == snapshot;
    public static bool IsSameForegroundWindow(R6ForegroundSnapshot snapshot)
    {
        var window = GetForegroundWindow();
        if (window != snapshot.Window) return false;
        GetWindowThreadProcessId(window, out uint id);
        return id == snapshot.ProcessId;
    }
}
