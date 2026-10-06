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
        Environment.SetEnvironmentVariable("AUTOAIMMY_VERSION", "0.3.0-preview");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var page = new AutoAimmyMenuControl();
        typeof(AutoAimmyMenuControl).GetField("lastLearningRefresh", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, DateTime.UtcNow);
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
        // Simulate a periodic status read already in flight when a user requests image import.
        var inspecting = typeof(AutoAimmyMenuControl).GetField("inspecting", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var refresh = typeof(AutoAimmyMenuControl).GetMethod("RefreshLearningAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        inspecting.SetValue(page, true);
        var import = (Task)refresh.Invoke(page, new object[] { true })!;
        if (import.IsCompleted) throw new Exception("Explicit image import was skipped during status refresh.");
        var importWatch = System.Diagnostics.Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (importWatch.ElapsedMilliseconds >= 80) inspecting.SetValue(page, false);
            if (import.IsCompleted || importWatch.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        if (!import.IsCompleted) throw new Exception("Queued image import did not resume.");
        import.GetAwaiter().GetResult();
        var assembly = typeof(AutoAimmyMenuControl).Assembly;
        // Construct the same three consumers as calibration without capturing any pixels.
        // They must share one DXGI owner, and closing recording must not dispose detection.
        var captureType = assembly.GetType("AILogic.CaptureManager")!;
        var sharedField = captureType.GetField("sharedBackend", BindingFlags.Static | BindingFlags.NonPublic)!;
        var clientsField = captureType.GetField("sharedClients", BindingFlags.Static | BindingFlags.NonPublic)!;
        var backendField = captureType.GetField("backend", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var disposedField = captureType.GetField("disposed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int baselineClients = (int)clientsField.GetValue(null)!;
        var detection = (IDisposable)Activator.CreateInstance(captureType)!;
        var hud = (IDisposable)Activator.CreateInstance(captureType)!;
        var recording = (IDisposable)Activator.CreateInstance(captureType)!;
        var shared = backendField.GetValue(detection)!;
        if (!ReferenceEquals(shared, backendField.GetValue(hud)) || !ReferenceEquals(shared, backendField.GetValue(recording)))
            throw new Exception("Calibration consumers create separate desktop-duplication owners.");
        var captureGate = captureType.GetField("SharedCaptureLock", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var displayChanged = captureType.GetMethod("OnDisplayChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        lock (captureGate)
        {
            var displayNotification = Task.Run(() => displayChanged.Invoke(shared, new object?[] { null, null }));
            if (!displayNotification.Wait(TimeSpan.FromSeconds(1))) throw new Exception("Display notification waits for capture and risks lock inversion.");
        }
        recording.Dispose(); recording.Dispose();
        if ((bool)disposedField.GetValue(shared)! || (int)clientsField.GetValue(null)! != baselineClients + 2)
            throw new Exception("Stopping recording destroys the active capture owner or releases twice.");
        Parallel.For(0, 128, _ =>
        {
            using var consumer = (IDisposable)Activator.CreateInstance(captureType)!;
            if (!ReferenceEquals(shared, backendField.GetValue(consumer))) throw new Exception("Concurrent capture ownership diverged.");
        });
        hud.Dispose(); detection.Dispose();
        if ((int)clientsField.GetValue(null)! != baselineClients || (baselineClients == 0 && sharedField.GetValue(null) != null))
            throw new Exception("Capture ownership leaks after the last consumer closes.");
        try
        {
            captureType.GetMethod("ScreenGrab")!.Invoke(recording, new object[] { new System.Drawing.Rectangle(0, 0, 1, 1), false, true });
            throw new Exception("Disposed capture consumer accepted a request.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is ObjectDisposedException) { }
        var runtimeType = assembly.GetType("Aimmy2.LocalAutomation.LocalAutomationSession")!;
        var runtime = runtimeType.GetProperty("Instance")!.GetValue(null)!;
        var state = runtimeType.GetProperty("State")!.GetValue(runtime)!;
        if ((bool)state.GetType().GetProperty("AssistanceEnabled")!.GetValue(state)!) throw new Exception("Mouse output armed at startup.");
        try { runtimeType.GetMethod("EnableAssistance")!.Invoke(runtime, new object[] { true }); throw new Exception("Uncalibrated output allowed."); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
        var engineField = runtimeType.GetField("engine", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var engine = (Aimmy2.AdaptiveControl.AdaptiveAimEngine)engineField.GetValue(runtime)!;
        engine.SetCalibration(new Aimmy2.AdaptiveControl.CalibrationResult { Success = true, ContextKey = "test", ScreenHeight = 1080,
            PixelsPerCountX = -2, PixelsPerCountY = -3, FitX = 1, FitY = 1, SamplesX = 12, SamplesY = 12, DurationSeconds = 2 });
        var activeField = runtimeType.GetField("active", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var needsField = runtimeType.GetField("viewNeedsMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)!;
        activeField.SetValue(runtime, true); needsField.SetValue(runtime, true);
        runtimeType.GetMethod("EnableAssistance")!.Invoke(runtime, new object[] { true });
        bool StateFlag(string flag) { var value = runtimeType.GetProperty("State")!.GetValue(runtime)!; return (bool)value.GetType().GetProperty(flag)!.GetValue(value)!; }
        if (StateFlag("AssistanceEnabled") || !StateFlag("NeedsMeasurement") || !(bool)runtimeType.GetProperty("AssistanceRequested")!.GetValue(runtime)!)
            throw new Exception("Pending camera measurement does not block stored calibration output.");
        needsField.SetValue(runtime, false);
        runtimeType.GetMethod("EnableAssistance")!.Invoke(runtime, new object[] { true });
        if (!StateFlag("AssistanceEnabled")) throw new Exception("Valid measured view cannot arm.");
        runtimeType.GetMethod("PauseFrame")!.Invoke(runtime, new object[] { "Synthetic foreground pause" });
        runtimeType.GetMethod("EnableAssistance")!.Invoke(runtime, new object[] { true });
        if (StateFlag("AssistanceEnabled") || !StateFlag("NeedsMeasurement")) throw new Exception("Short foreground pause permits stale camera calibration.");
        runtimeType.GetMethod("EnableAssistance")!.Invoke(runtime, new object[] { false });
        activeField.SetValue(runtime, false);
        Console.WriteLine("PASS: old calibration stays disarmed after a short pause or pending view measurement, including a second activation request.");
        // A non-pumping UI synchronization context exposes shutdown continuations that would deadlock.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        runtimeType.GetMethod("Shutdown")!.Invoke(runtime, null);
        if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new Exception("Idle local shutdown blocked.");
        Console.WriteLine("PASS: shared capture lifecycle/concurrent consumers, dashboard controls/rendering, queued import, default output disarmed, uncalibrated refusal and UI-context shutdown. No game capture or generated input.");
        Console.WriteLine("UI renders: " + output);
        app.Shutdown();
    }
}
