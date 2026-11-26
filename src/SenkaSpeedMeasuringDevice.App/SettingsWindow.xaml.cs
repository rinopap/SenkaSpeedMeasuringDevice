using System.Windows;
using SenkaSpeedMeasuringDevice.Capture;
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
        CdpEnabledCheck.IsChecked = settings.CdpEnabled;
        CdpPortBox.Text = settings.CdpDebugPort.ToString();
        CdpPageUrlBox.Text = settings.CdpPageUrl;
        CdpApiHostsBox.Text = settings.CdpApiHosts;
        FontSizeValue.Text = ((int)FontSizeSlider.Value).ToString();
        FontSizeSlider.ValueChanged += (_, _) => FontSizeValue.Text = ((int)FontSizeSlider.Value).ToString();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CdpPortBox.Text, out var cdpPort) || cdpPort is < 1 or > 65535)
        {
            System.Windows.MessageBox.Show(this, "CDPポートは1～65535で指定してください。", "設定",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var apiHosts = CdpApiHostsBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var cdpPageUrl = CdpPageUrlBox.Text.Trim();
        if (CdpEnabledCheck.IsChecked == true)
        {
            try
            {
                new CdpCaptureOptions(cdpPort, cdpPageUrl,
                    new HashSet<string>(apiHosts, StringComparer.OrdinalIgnoreCase)).Validate();
            }
            catch (ArgumentException ex)
            {
                System.Windows.MessageBox.Show(this, ex.Message, "CDP設定", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        var settings = new AppSettings
        {
            DarkMode = DarkModeCheck.IsChecked == true,
            FontFamily = FontFamilyBox.SelectedItem as string ?? "Yu Gothic",
            FontSize = (int)FontSizeSlider.Value,
            AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true,
            CdpEnabled = CdpEnabledCheck.IsChecked == true,
            CdpDebugPort = cdpPort,
            CdpPageUrl = cdpPageUrl,
            CdpApiHosts = string.Join(',', apiHosts)
        };
        SettingsApplied?.Invoke(settings, AutoStartCheck.IsChecked == true);
        DialogResult = true;
    }
}

