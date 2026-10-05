using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Aimmy2.Adaptive;

/// <summary>Passive standard Windows Raw Input, mouse only. No input generation or game access.</summary>
internal sealed class RawMouseObserver : IDisposable
{
    // Windows permits one Raw Input registration per device class in a process.
    // Observation and adaptive calibration therefore share the same passive source.
    private static readonly Lazy<RawMouseObserver> shared = new(() => new RawMouseObserver());
    public static RawMouseObserver Shared => shared.Value;
    public static void DisposeShared() { if (shared.IsValueCreated) shared.Value.Dispose(); }
    [StructLayout(LayoutKind.Sequential)]
    private struct Device { public ushort Page, Usage; public uint Flags; public IntPtr Window; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Header { public uint Type, Size; public IntPtr Device, Parameter; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Mouse
    {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(4)] public ushort Buttons;
        [FieldOffset(12)] public int X;
        [FieldOffset(16)] public int Y;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(Device[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);

    private readonly object gate = new();
    private readonly Thread thread;
    private readonly ManualResetEventSlim initialized = new();
    private Dispatcher? dispatcher;
    private long x, y;
    private bool leftButton;
    private volatile bool available;
    private bool disposed;
    public string? Failure { get; private set; }

    public RawMouseObserver()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "AutoAimmy mouse observer" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        initialized.Wait();
    }

    public bool TryRead(out long totalX, out long totalY, out bool click)
    {
        lock (gate) { totalX = x; totalY = y; click = leftButton; }
        return available;
    }

    private void Run()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            using var source = new HwndSource(new HwndSourceParameters("AutoAimmy observation")
            { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
            buffer = Marshal.AllocHGlobal(256);
            uint headerSize = (uint)Marshal.SizeOf<Header>();
            source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message != 0x00FF) return IntPtr.Zero;
                uint size = 256;
                uint received = GetRawInputData(lParam, 0x10000003, buffer, ref size, headerSize);
                if (received == uint.MaxValue || received < headerSize + Marshal.SizeOf<Mouse>()) return IntPtr.Zero;
                var header = Marshal.PtrToStructure<Header>(buffer);
                if (header.Type != 0) return IntPtr.Zero;
                var mouse = Marshal.PtrToStructure<Mouse>(IntPtr.Add(buffer, (int)headerSize));
                lock (gate)
                {
                    // Absolute tablet coordinates are not relative mouse counts.
                    if ((mouse.Flags & 1) == 0) { x += mouse.X; y += mouse.Y; }
                    if ((mouse.Buttons & 1) != 0) leftButton = true;
                    if ((mouse.Buttons & 2) != 0) leftButton = false;
                }
                return IntPtr.Zero;
            });
            if (!RegisterRawInputDevices(new[] { new Device { Page = 1, Usage = 2, Flags = 0x100, Window = source.Handle } },
                1, (uint)Marshal.SizeOf<Device>()))
                throw new InvalidOperationException($"Raw Input registration failed ({Marshal.GetLastWin32Error()}).");
            available = true;
            initialized.Set();
            Dispatcher.Run();
            RegisterRawInputDevices(new[] { new Device { Page = 1, Usage = 2, Flags = 1, Window = IntPtr.Zero } },
                1, (uint)Marshal.SizeOf<Device>());
        }
        catch (Exception ex) { Failure = ex.Message; }
        finally
        {
            available = false;
            initialized.Set();
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        initialized.Wait();
        if (thread.IsAlive && dispatcher is { HasShutdownStarted: false }) dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        thread.Join();
        initialized.Dispose();
    }
}
