namespace Aimmy2.UILibrary
{
    /// <summary>
    /// Interaction logic for APButton.xaml
    /// </summary>
    public partial class APButton : System.Windows.Controls.UserControl
    {
        public APButton(string Text, string? tooltip = null, string iconGlyph = "\uE8B0")
        {
            InitializeComponent();
            ButtonTitle.Content = Text;
            IconLabel.Content = iconGlyph;

            if (!string.IsNullOrEmpty(tooltip))
            {
                var tt = new System.Windows.Controls.ToolTip { Content = tooltip };
                if (TryFindResource("Tooltip") is System.Windows.Style style)
                    tt.Style = style;
                ToolTip = tt;
            }
        }
    }
}
