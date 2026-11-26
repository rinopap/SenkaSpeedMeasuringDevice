using System.Windows;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.App;

public partial class SettingsWindow : Window
{
    public event Action<AppSettings, bool>? SettingsApplied;

    public SettingsWindow(AppSettings settings, bool autoStart)
    {
        InitializeComponent();
        FontFamilyBox.ItemsSource = new[] { "Yu Gothic", "メイリオ", "MS Gothic", "Arial", "Courier New" };
        FontFamilyBox.SelectedItem = FontFamilyBox.Items.Contains(settings.FontFamily) ? settings.FontFamily : "Yu Gothic";
        DarkModeCheck.IsChecked = settings.DarkMode;
        FontSizeSlider.Value = Math.Clamp(settings.FontSize, 8, 24);
        AlwaysOnTopCheck.IsChecked = settings.AlwaysOnTop;
        AutoStartCheck.IsChecked = autoStart;
        FontSizeValue.Text = ((int)FontSizeSlider.Value).ToString();
        FontSizeSlider.ValueChanged += (_, _) => FontSizeValue.Text = ((int)FontSizeSlider.Value).ToString();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var settings = new AppSettings
        {
            DarkMode = DarkModeCheck.IsChecked == true,
            FontFamily = FontFamilyBox.SelectedItem as string ?? "Yu Gothic",
            FontSize = (int)FontSizeSlider.Value,
            AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true
        };
        SettingsApplied?.Invoke(settings, AutoStartCheck.IsChecked == true);
        DialogResult = true;
    }
}

