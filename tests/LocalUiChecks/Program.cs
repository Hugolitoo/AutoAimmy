using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Aimmy2.Controls;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "AutoAimmy-ui-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("AUTOAIMMY_DATA_DIR", Path.Combine(output, "test-data"));
        Environment.SetEnvironmentVariable("AUTOAIMMY_VERSION", "0.2.0-preview");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var page = new AutoAimmyMenuControl();
        typeof(AutoAimmyMenuControl).GetMethod("RefreshView", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
        var host = new Border { Background = new SolidColorBrush(Color.FromRgb(26, 15, 43)), Child = page, Width = 1050, Height = 800 };
        host.Measure(new Size(1050, 800)); host.Arrange(new Rect(0, 0, 1050, 800)); host.UpdateLayout();
        var scroll = page.DashboardScrollViewer;
        foreach (var position in new[] { ("top", 0d), ("learning", 700d), ("bottom", scroll.ExtentHeight) })
        {
            scroll.ScrollToVerticalOffset(position.Item2); host.UpdateLayout();
            var rendered = new RenderTargetBitmap(1050, 800, 96, 96, PixelFormats.Pbgra32); rendered.Render(host);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
            using var stream = File.Create(Path.Combine(output, position.Item1 + ".png")); encoder.Save(stream);
        }
        if (scroll.ExtentHeight <= scroll.ViewportHeight) throw new Exception("Dashboard should scroll at desktop viewport size.");
        foreach (string button in new[] { "StartLocalButton", "CalibrateLocalButton", "AssistLocalButton", "ReviewLearningButton", "OptimizeLearningButton", "StartTrainingButton" })
            if (page.FindName(button) is not Button) throw new Exception("Missing workflow control: " + button);
        var assembly = typeof(AutoAimmyMenuControl).Assembly;
        var runtimeType = assembly.GetType("Aimmy2.LocalAutomation.LocalAutomationSession")!;
        var runtime = runtimeType.GetProperty("Instance")!.GetValue(null)!;
        var state = runtimeType.GetProperty("State")!.GetValue(runtime)!;
        if ((bool)state.GetType().GetProperty("AssistanceEnabled")!.GetValue(state)!) throw new Exception("Mouse output armed at startup.");
        try { runtimeType.GetMethod("EnableAssistance")!.Invoke(runtime, new object[] { true }); throw new Exception("Uncalibrated output allowed."); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
        // A non-pumping UI synchronization context exposes shutdown continuations that would deadlock.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        runtimeType.GetMethod("Shutdown")!.Invoke(runtime, null);
        if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new Exception("Idle local shutdown blocked.");
        Console.WriteLine("PASS: dashboard controls/rendering, default output disarmed, uncalibrated refusal and UI-context shutdown. No game capture or generated input.");
        Console.WriteLine("UI renders: " + output);
        app.Shutdown();
    }
}
