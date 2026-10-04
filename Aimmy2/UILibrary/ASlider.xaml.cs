using Aimmy2.Theme;
using System.Windows.Controls;
using System.Windows.Input;

namespace Aimmy2.UILibrary
{
    /// <summary>
    /// Interaction logic for ASlider.xaml
    /// </summary>
    public partial class ASlider : UserControl
    {
        private readonly string _notifierText;
        private Func<double, string>? _valueFormatter;

        public ASlider(string Text, string NotifierText, double ButtonSteps, string? tooltip = null)
        {
            InitializeComponent();

            _notifierText = NotifierText;
            SliderTitle.Content = Text;

            if (!string.IsNullOrEmpty(tooltip))
            {
                var tt = new System.Windows.Controls.ToolTip { Content = tooltip };
                if (TryFindResource("Tooltip") is System.Windows.Style style)
                    tt.Style = style;
                ToolTip = tt;
            }

            Slider.ValueChanged += (s, e) =>
            {
                UpdateNotifier();
            };

            SubtractOne.Click += (s, e) => UpdateSliderValue(-ButtonSteps);
            AddOne.Click += (s, e) => UpdateSliderValue(ButtonSteps);

            // Register buttons for theme updates when loaded
            Loaded += (s, e) =>
            {
                ThemeManager.RegisterElement(SubtractOne);
                ThemeManager.RegisterElement(AddOne);
            };
        }

        private void UpdateSliderValue(double change)
        {
            Slider.Value = Math.Round(Slider.Value + change, 2);
        }

        public void SetValueFormatter(Func<double, string> valueFormatter)
        {
            _valueFormatter = valueFormatter;
            UpdateNotifier();
        }

        private void UpdateNotifier()
        {
            AdjustNotifier.Content = _valueFormatter?.Invoke(Slider.Value) ?? $"{Slider.Value:F2} {_notifierText}";
        }

        private void Slider_MouseUp(object sender, MouseButtonEventArgs e)
        {
        }

        private void Slider_MouseUp_1(object sender, MouseButtonEventArgs e)
        {
            System.Windows.MessageBox.Show($"{Slider.Value:F2}");
        }
    }
}
