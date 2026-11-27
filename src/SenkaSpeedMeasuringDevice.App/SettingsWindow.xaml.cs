using System.Windows;
using MessageBox = System.Windows.MessageBox;
using SenkaSpeedMeasuringDevice.Capture;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.App;

public partial class SettingsWindow : Window
{
    private readonly IReadOnlyList<BrowserInstallation> _browsers;
    private bool _updatingMode;

    public event Action<AppSettings, bool, bool>? SettingsApplied;
    public event Action<string>? StatusMessage;

    public SettingsWindow(AppSettings settings, bool autoStart)
    {
        InitializeComponent();
        _browsers = BrowserCatalog.FindSupportedInstalled();
        FontFamilyBox.ItemsSource = new[] { "Yu Gothic", "メイリオ", "MS Gothic", "Arial", "Courier New" };
        GameServerBox.ItemsSource = GameServerRegistry.All;
        BrowserBox.ItemsSource = _browsers;
        GameServerBox.SelectedValue = settings.ServerId;
        FontFamilyBox.SelectedItem = FontFamilyBox.Items.Contains(settings.FontFamily) ? settings.FontFamily : "Yu Gothic";
        DarkModeCheck.IsChecked = settings.DarkMode;
        FontSizeSlider.Value = Math.Clamp(settings.FontSize, 8, 24);
        AlwaysOnTopCheck.IsChecked = settings.AlwaysOnTop;
        AutoStartCheck.IsChecked = autoStart;
        CdpPortBox.Text = settings.CdpDebugPort.ToString();
        BrowserBox.SelectedItem = _browsers.FirstOrDefault(browser => browser.Id == settings.BrowserId)
                                  ?? _browsers.FirstOrDefault(browser => browser.Id == BrowserCatalog.GetDefaultBrowserId())
                                  ?? _browsers.FirstOrDefault();
        _updatingMode = true;
        DirectCaptureCheck.IsChecked = settings.EffectiveCaptureMode == "cdp";
        JsonCaptureCheck.IsChecked = settings.EffectiveCaptureMode == "json";
        _updatingMode = false;
        FontSizeValue.Text = ((int)FontSizeSlider.Value).ToString();
        FontSizeSlider.ValueChanged += (_, _) => FontSizeValue.Text = ((int)FontSizeSlider.Value).ToString();
        GameServerBox.SelectionChanged += (_, _) => UpdateApiHostText();
        UpdateBrowserUi();
        UpdateModeUi();
        UpdateApiHostText();
        BrowserSupportText.Text = _browsers.Count == 0
            ? "署名を確認できるChrome / Edgeが見つかりません。ブラウザから直接取得する場合は、詳細情報から手動接続先を設定してください。"
            : BrowserCatalog.DescribeUnverifiedInstalled();
    }

    private void DirectCapture_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingMode) return;
        _updatingMode = true;
        if (DirectCaptureCheck.IsChecked == true) JsonCaptureCheck.IsChecked = false;
        _updatingMode = false;
        UpdateModeUi();
    }

    private void JsonCapture_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingMode) return;
        _updatingMode = true;
        if (JsonCaptureCheck.IsChecked == true) DirectCaptureCheck.IsChecked = false;
        _updatingMode = false;
        UpdateModeUi();
    }

    private void UpdateModeUi()
    {
        var direct = DirectCaptureCheck.IsChecked == true;
        BrowserSettingsPanel.IsEnabled = direct;
        DetailsExpander.IsEnabled = direct;
        if (BrowserBox.SelectedItem is not BrowserInstallation)
        {
            PrepareLoginButton.IsEnabled = false;
            StartMeasurementButton.IsEnabled = false;
        }
    }

    private void UpdateApiHostText()
    {
        ApiHostText.Text = GameServerBox.SelectedItem is GameServer server
            ? $"選択APIホスト: https://{server.ApiHost}"
            : "選択APIホスト: 所属サーバー未選択";
    }

    private void Browser_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateBrowserUi();

    private void UpdateBrowserUi()
    {
        var selected = BrowserBox.SelectedItem as BrowserInstallation;
        PrepareLoginButton.IsEnabled = selected?.CanLaunch == true;
        StartMeasurementButton.IsEnabled = selected is not null;
    }

    private async void PrepareLogin_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserBox.SelectedItem is not BrowserInstallation browser)
        {
            MessageBox.Show(this, "利用可能な対応ブラウザを選択してください。", "ブラウザ起動",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!browser.CanLaunch)
        {
            MessageBox.Show(this, "74ENはこのログイン準備機能から起動・終了しません。既存の74EN内で利用者自身が認証してください。",
                "ログイン準備", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var consent = MessageBox.Show(this,
            $"{browser.Name}をCDP接続なしで艦これ専用プロファイルにて起動します。認証は利用者がブラウザ上で行います。\n\n" +
            "専用プロファイルのログイン状態はブラウザが管理します。計測機はCookie、パスワード、APIトークンを読み取りません。通常の利用プロファイルから認証情報を移すことはありません。\n\n起動しますか？",
            "ログイン準備", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (consent != MessageBoxResult.Yes) return;

        PrepareLoginButton.IsEnabled = false;
        try
        {
            var result = await BrowserLaunchHelper.PrepareLoginAsync(browser);
            if (result.Success) StatusMessage?.Invoke("ログイン準備中: CDP接続なし。認証状態は確認しません");
            MessageBox.Show(this, result.Message, "ログイン準備", MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally { PrepareLoginButton.IsEnabled = BrowserBox.SelectedItem is BrowserInstallation selected && selected.CanLaunch; }
    }

    private async void StartMeasurement_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserBox.SelectedItem is not BrowserInstallation browser)
        {
            MessageBox.Show(this, "利用可能な接続先を選択してください。", "計測開始",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (GameServerBox.SelectedItem is not GameServer)
        {
            MessageBox.Show(this, "所属サーバーを選択してください。", "計測開始",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!browser.CanLaunch)
        {
            var consent = MessageBox.Show(this,
                "選択した接続先のCDPへ接続し、計測を開始します。ブラウザ側のCDP設定と接続先が意図した対象であることを確認してください。計測は正規ゲームページの母港APIを受信するまで待機します。\n\n開始しますか？",
                "計測開始の確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (consent == MessageBoxResult.Yes) Apply_Click(startMeasurement: true);
            return;
        }

        var consentForCdp = MessageBox.Show(this,
            $"{browser.Name}を同じ専用プロファイルでCDP付き起動し、計測を開始します。CDPにはブラウザのページや通信を扱える強いデバッグ権限があります。認証ページの内容は取得しませんが、ページ遷移後もブラウザのデバッグ待受自体は終了まで残ります。計測機はブラウザを終了せず、認証状態も確認しません。\n\n開始する前にログイン準備用のブラウザを利用者自身で閉じてください。既に起動中の場合、計測機は終了や再起動を行いません。\n\n計測を開始しますか？",
            "CDP計測の開始確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (consentForCdp != MessageBoxResult.Yes) return;

        StartMeasurementButton.IsEnabled = false;
        try
        {
            var result = await BrowserLaunchHelper.LaunchAsync(browser);
            if (!result.Success || result.Port is null)
            {
                MessageBox.Show(this, result.Message, "計測開始", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            StatusMessage?.Invoke("CDP接続中: 対象API待機中");
            CdpPortBox.Text = result.Port.Value.ToString();
            _updatingMode = true;
            DirectCaptureCheck.IsChecked = true;
            JsonCaptureCheck.IsChecked = false;
            _updatingMode = false;
            Apply_Click(startMeasurement: true);
        }
        finally { StartMeasurementButton.IsEnabled = BrowserBox.SelectedItem is BrowserInstallation; }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Apply_Click(startMeasurement: false);

    private void Apply_Click(bool startMeasurement)
    {
        if (!int.TryParse(CdpPortBox.Text, out var cdpPort) || cdpPort is < 1 or > 65535)
        {
            System.Windows.MessageBox.Show(this, "CDPポートは1～65535で指定してください。", "設定",
                MessageBoxButton.OK, MessageBoxImage.Information);
            DetailsExpander.IsExpanded = true;
            return;
        }

        var direct = DirectCaptureCheck.IsChecked == true;
        var json = JsonCaptureCheck.IsChecked == true;
        var mode = direct && !json ? "cdp" : json && !direct ? "json" : "off";
        int? serverId = GameServerBox.SelectedValue is int selectedServerId ? selectedServerId : null;
        if (direct)
        {
            if (serverId is null)
            {
                System.Windows.MessageBox.Show(this, "所属サーバーを選択してください。", "設定",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (BrowserBox.SelectedItem is not BrowserInstallation)
            {
                System.Windows.MessageBox.Show(this, "署名を確認できるChromeまたはEdgeを選択してください。", "設定",
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
            CdpEnabled = mode == "cdp",
            CdpDebugPort = cdpPort,
            ServerId = serverId,
            CaptureMode = mode,
            BrowserId = BrowserBox.SelectedItem is BrowserInstallation selectedBrowser ? selectedBrowser.Id : null
        };
        SettingsApplied?.Invoke(settings, AutoStartCheck.IsChecked == true, startMeasurement);
        DialogResult = true;
    }
}
