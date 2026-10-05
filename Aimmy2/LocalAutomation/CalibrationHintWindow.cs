using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Aimmy2.LocalAutomation;

internal sealed class CalibrationHintWindow : Window
{
    private readonly TextBlock text = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(14) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTime? completed;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    public CalibrationHintWindow()
    {
        Title = "AutoAimmy — calibration";
        Width = 490; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(230, 27, 19, 42));
        ShowActivated = false; ShowInTaskbar = false; Topmost = true; IsHitTestVisible = false;
        Left = Aimmy2.Class.DisplayManager.ScreenLeft / global::Class.WinAPICaller.scalingFactorX + 16;
        Top = Aimmy2.Class.DisplayManager.ScreenTop / global::Class.WinAPICaller.scalingFactorY + 16;
        Content = text;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x20 | 0x80 | 0x08000000);
        };
        timer.Tick += (_, _) =>
        {
            var state = LocalAutomationSession.Instance.State;
            text.Text = "AutoAimmy · " + state.Message;
            if (!state.Calibrating) completed ??= DateTime.UtcNow;
            if (completed.HasValue && DateTime.UtcNow - completed.Value > TimeSpan.FromSeconds(5)) Close();
        };
        Closed += (_, _) => timer.Stop();
        timer.Start();
    }
}
