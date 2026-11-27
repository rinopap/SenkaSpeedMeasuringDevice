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
        GameServerBox.ItemsSource = GameServerRegistry.All;
        GameServerBox.SelectedValue = settings.ServerId;
        FontFamilyBox.SelectedItem = FontFamilyBox.Items.Contains(settings.FontFamily) ? settings.FontFamily : "Yu Gothic";
        DarkModeCheck.IsChecked = settings.DarkMode;
        FontSizeSlider.Value = Math.Clamp(settings.FontSize, 8, 24);
        AlwaysOnTopCheck.IsChecked = settings.AlwaysOnTop;
        AutoStartCheck.IsChecked = autoStart;
        CdpEnabledCheck.IsChecked = settings.CdpEnabled;
        CdpPortBox.Text = settings.CdpDebugPort.ToString();
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

        int? serverId = GameServerBox.SelectedValue is int selectedServerId ? selectedServerId : null;
        if (CdpEnabledCheck.IsChecked == true)
        {
            if (serverId is null)
            {
                System.Windows.MessageBox.Show(this, "所属サーバーを選択してください。", "設定",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try { new CdpCaptureOptions(cdpPort, serverId.Value).Validate(); }
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
            ServerId = serverId
        };
        SettingsApplied?.Invoke(settings, AutoStartCheck.IsChecked == true);
        DialogResult = true;
    }
}
