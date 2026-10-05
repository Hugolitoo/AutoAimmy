using Aimmy2.Adaptive;
using Aimmy2.Class;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Aimmy2.VisualAnalysis;

internal sealed class LiveHudObserver : IDisposable
{
    public static LiveHudObserver Instance { get; } = new();
    private readonly CancellationTokenSource stop = new();
    private Task? worker;
    private VisualHudObservation latest = new(DateTime.UtcNow, "Starting", null, null);
    public VisualHudObservation Latest => Volatile.Read(ref latest);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    public void Start() { if (worker == null) worker = Task.Run(ObserveAsync); }
    private static bool GameInForeground(out Rectangle gameBounds)
    {
        gameBounds = Rectangle.Empty;
        var window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out uint id);
        try
        {
            using var process = Process.GetProcessById((int)id);
            bool game = process.ProcessName.Equals("RainbowSix", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("RainbowSix_Vulkan", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("RainbowSix_DX11", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.Equals("RainbowSix_DX12", StringComparison.OrdinalIgnoreCase);
            if (game && GetWindowRect(window, out var rectangle))
                gameBounds = Rectangle.FromLTRB(rectangle.Left,rectangle.Top,rectangle.Right,rectangle.Bottom);
            return game && !gameBounds.IsEmpty;
        }
        catch { return false; }
    }
    public static async Task<string> ReadTextAsync(Bitmap bitmap)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? throw new InvalidOperationException("Aucune langue OCR Windows disponible.");
        using var inverted = new Bitmap(bitmap.Width, bitmap.Height);
        using (var graphics = Graphics.FromImage(inverted))
        using (var attributes = new ImageAttributes())
        {
            attributes.SetColorMatrix(new ColorMatrix(new[] {
                new float[] {-1,0,0,0,0}, new float[] {0,-1,0,0,0}, new float[] {0,0,-1,0,0},
                new float[] {0,0,0,1,0}, new float[] {1,1,1,0,1} }));
            graphics.DrawImage(bitmap, new Rectangle(0,0,bitmap.Width,bitmap.Height), 0,0,bitmap.Width,bitmap.Height,GraphicsUnit.Pixel,attributes);
        }
        using var encoded = new MemoryStream();
        inverted.Save(encoded, ImageFormat.Bmp);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(encoded.ToArray());
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var software = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(software);
        return result.Text;
    }
    private async Task ObserveAsync()
    {
        string? priorWeapon = null, priorScope = null;
        int weaponStreak = 0, scopeStreak = 0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                if (!GameInForeground(out var gameBounds))
                {
                    priorWeapon = priorScope = null; weaponStreak = scopeStreak = 0;
                    Volatile.Write(ref latest, Latest with { ReadUtc=DateTime.UtcNow, Status="WaitingForGame", Detail="Lecture en pause : R6 doit être au premier plan. Les valeurs ci-dessous sont le dernier texte lu, pas une détection actuelle." });
                    continue;
                }
                try
                {
                    // Only the lower-right HUD of the selected monitor; never store frames or OCR transcripts.
                    int width = DisplayManager.ScreenWidth, height = DisplayManager.ScreenHeight;
                    var bounds = Rectangle.Intersect(gameBounds, new Rectangle(DisplayManager.ScreenLeft,DisplayManager.ScreenTop,width,height));
                    if (bounds.Width < 320 || bounds.Height < 200) {
                        Volatile.Write(ref latest, new(DateTime.UtcNow,"WrongMonitor",null,null,Detail:"Choisissez dans Aimmy le moniteur où R6 est affiché."));
                        continue;
                    }
                    int cropWidth = Math.Max(1, bounds.Width * 2 / 5), cropHeight = Math.Max(1, bounds.Height * 2 / 5);
                    using var capture = new Bitmap(cropWidth, cropHeight, PixelFormat.Format32bppArgb);
                    using (var graphics = Graphics.FromImage(capture))
                        graphics.CopyFromScreen(bounds.Right - cropWidth, bounds.Bottom - cropHeight, 0, 0, capture.Size);
                    double scale = Math.Min(1, 1600.0 / Math.Max(cropWidth, cropHeight));
                    using var resized = new Bitmap(capture, new Size(Math.Max(1, (int)(cropWidth * scale)), Math.Max(1, (int)(cropHeight * scale))));
                    var parsed = HudTextParser.Parse(await ReadTextAsync(resized));
                    weaponStreak = parsed.Weapon != null && parsed.Weapon == priorWeapon ? weaponStreak + 1 : parsed.Weapon == null ? 0 : 1;
                    scopeStreak = parsed.Scope != null && parsed.Scope == priorScope ? scopeStreak + 1 : parsed.Scope == null ? 0 : 1;
                    priorWeapon = parsed.Weapon; priorScope = parsed.Scope;
                    Volatile.Write(ref latest, new(DateTime.UtcNow, "ReadingHUD", weaponStreak >= 2 ? parsed.Weapon : null, scopeStreak >= 2 ? parsed.Scope : null,
                        Detail:"Texte du HUD lu localement (1 lecture/s, 2 lectures cohérentes). Les icônes seules et l’état ADS ne sont pas reconnus.", FrameUtc:DateTime.UtcNow));
                }
                catch (Exception error)
                {
                    priorWeapon = priorScope = null; weaponStreak = scopeStreak = 0;
                    Volatile.Write(ref latest, new(DateTime.UtcNow, "Unavailable", null, null, Detail:error.GetType().Name + ": " + error.Message));
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public void Dispose() { stop.Cancel(); }
}
