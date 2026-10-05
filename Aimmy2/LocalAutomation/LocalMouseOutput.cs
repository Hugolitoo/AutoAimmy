using Aimmy2.Class;
using Aimmy2.LocalCapture;
using Aimmy2.MouseMovementLibraries.GHubSupport;
using MouseMovementLibraries.ddxoftSupport;
using MouseMovementLibraries.RazerSupport;
using System.Runtime.InteropServices;

namespace Aimmy2.LocalAutomation;

/// <summary>Separate bounded relative-count path; never uses the legacy uncalibrated pixel conversion.</summary>
internal static class LocalMouseOutput
{
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] input, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    internal static bool TryMove(int x, int y, R6ForegroundSnapshot foreground, out string? error)
    {
        error = null;
        var status = LocalAutomationSession.Instance.State;
        if (!status.Active || !status.AssistanceEnabled || status.Calibrating || !status.Calibrated ||
            !LocalAutomationSession.ActivationHeld || !R6ForegroundGuard.StillMatches(foreground) ||
            (GetAsyncKeyState(0x77) & 0x8000) != 0) return true;
        if (Math.Abs((long)x) > 24 || Math.Abs((long)y) > 24) { error = "Correction hors limites."; return false; }
        if (x == 0 && y == 0) return true;
        try
        {
            switch (AimSettings.MouseMovementMethod)
            {
                case "LG HUB": LGMouse.Move(0, x, y, 0); break;
                case "Razer Synapse (Require Razer Peripheral)": RZMouse.mouse_move(x, y, true); break;
                case "ddxoft Virtual Input Driver":
                    if (DdxoftMain.ddxoftInstance.movR == null) throw new InvalidOperationException("Pilote de souris non disponible.");
                    DdxoftMain.ddxoftInstance.movR(x, y); break;
                default:
                    var input = new NativeInput { Type = 0, Data = new InputUnion { Mouse = new MouseInput { X = x, Y = y, Flags = 1 } } };
                    if (SendInput(1, new[] { input }, Marshal.SizeOf<NativeInput>()) != 1)
                        throw new InvalidOperationException("Windows n’a pas accepté le déplacement de souris.");
                    break;
            }
            return true;
        }
        catch (Exception failure) { error = failure.Message; return false; }
    }
}
