using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using SenkaSpeedMeasuringDevice.Capture;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.App;

public partial class MainWindow : Window
{
    private readonly string _appDirectory = AppContext.BaseDirectory;
    private readonly List<ExperienceSample> _admiralSamples;
    private readonly List<ExperienceSample> _shipSamples;
    private readonly object _samplesLock = new();
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _displayTimer;
    private AppSettings _settings;
    private ICaptureSource? _captureSource;
    private string? _captureSetupError;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load(Path.Combine(_appDirectory, "ui_config.json"));
        _admiralSamples = CsvStorage.Load(Path.Combine(_appDirectory, "exp_log.csv"), "admiral_exp").ToList();
        _shipSamples = CsvStorage.Load(Path.Combine(_appDirectory, "ship_exp_log.csv"), "ship_total_exp").ToList();
        try { SetCaptureSource(CreateCaptureSource(_settings)); }
        catch (ArgumentException ex) { _captureSetupError = ex.Message; }

        _trayIcon = CreateTrayIcon();
        _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _displayTimer.Tick += (_, _) => RefreshDisplays();
        _displayTimer.Start();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        ApplySettings(_settings);
        RefreshDisplays();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_captureSource is null)
        {
            SetCaptureStatus($"CDP設定エラー: {_captureSetupError}");
            return;
        }
        try
        {
            await _captureSource.StartAsync(CancellationToken.None);
            if (_captureSource is FileCaptureSource)
                SetCaptureStatus($"74EN JSON監視: {Path.Combine(_appDirectory, "74en_responses")}");
            else
                SetCaptureStatus("CDP接続待機中");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetCaptureStatus("データ取得を開始できません");
        }
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("ウィンドウを開く", null, (_, _) => ShowWindow());
        menu.Items.Add("設定", null, (_, _) => OpenSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApplication());
        var icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "戦果時速計測機",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => ShowWindow();
        return icon;
    }

    private void SetCaptureSource(ICaptureSource source)
    {
        _captureSource = source;
        _captureSetupError = null;
        source.ResponseReceived += OnResponseReceived;
        if (source is CdpCaptureSource cdp) cdp.StatusChanged += OnCaptureStatusChanged;
    }

    private ICaptureSource CreateCaptureSource(AppSettings settings)
    {
        if (!settings.CdpEnabled) return new FileCaptureSource(Path.Combine(_appDirectory, "74en_responses"));
        var hosts = settings.CdpApiHosts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var options = new CdpCaptureOptions(settings.CdpDebugPort, settings.CdpPageUrl,
            new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase));
        return new CdpCaptureSource(options);
    }

    private void OnCaptureStatusChanged(object? sender, string status) =>
        Dispatcher.InvokeAsync(() => SetCaptureStatus(status));

    private void OnResponseReceived(object? sender, ApiResponse response)
    {
        var snapshot = ApiResponseParser.Parse(response);
        if (snapshot is null) return;
        var storageFailed = false;
        lock (_samplesLock)
        {
            if (snapshot.AdmiralExperience is { } admiral)
            {
                var sample = new ExperienceSample(snapshot.Timestamp, admiral);
                try { CsvStorage.Append(Path.Combine(_appDirectory, "exp_log.csv"), "admiral_exp", sample); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { storageFailed = true; }
                _admiralSamples.Add(sample);
            }
            if (snapshot.ShipTotalExperience is { } shipTotal)
            {
                var sample = new ExperienceSample(snapshot.Timestamp, shipTotal);
                try { CsvStorage.Append(Path.Combine(_appDirectory, "ship_exp_log.csv"), "ship_total_exp", sample); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { storageFailed = true; }
                _shipSamples.Add(sample);
            }
        }

        Dispatcher.InvokeAsync(() =>
        {
            SetCaptureStatus(storageFailed ? "CSV書き込みエラー: データフォルダを確認してください" :
                _settings.CdpEnabled ? "CDP接続中: 計測中" : "74EN JSON監視: 計測中");
            RefreshDisplays();
        });
    }

    private void SetCaptureStatus(string details)
    {
        if (MainTabs.Template.FindName("CaptureStatus", MainTabs) is not System.Windows.Controls.TextBlock status)
            return;

        status.Text = details switch
        {
            _ when details.StartsWith("74EN JSON監視", StringComparison.Ordinal) => "74EN JSON監視",
            _ when details.StartsWith("CDP接続中", StringComparison.Ordinal) && details.Contains("計測中", StringComparison.Ordinal) => "CDP計測中",
            _ when details.StartsWith("CDP接続中", StringComparison.Ordinal) => "CDP接続中",
            _ when details.StartsWith("CDP接続先を確認中", StringComparison.Ordinal) => "CDP確認中",
            _ when details.StartsWith("CDP接続待機中", StringComparison.Ordinal) => "CDP待機中",
            _ when details.StartsWith("CDP停止", StringComparison.Ordinal) => "CDP停止",
            _ when details.StartsWith("CDP設定エラー", StringComparison.Ordinal) => "CDP設定エラー",
            _ when details.StartsWith("CSV書き込みエラー", StringComparison.Ordinal) => "CSVエラー",
            _ when details.Contains("開始できません", StringComparison.Ordinal) => "取得エラー",
            _ => details
        };
        status.ToolTip = details;
    }

    private void RefreshDisplays()
    {
        List<ExperienceSample> admiral;
        List<ExperienceSample> ships;
        lock (_samplesLock)
        {
            admiral = [.. _admiralSamples];
            ships = [.. _shipSamples];
        }
        var now = DateTimeOffset.Now;
        AdmiralDisplay.Text = SenkaCalculator.FormatThreeHours(admiral, now, ship: false);
        ShipDisplay.Text = SenkaCalculator.FormatThreeHours(ships, now, ship: true);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        var dialog = new SettingsWindow(_settings, WindowsStartup.IsEnabled()) { Owner = this };
        dialog.SettingsApplied += OnSettingsApplied;
        dialog.ShowDialog();
    }

    private async void OnSettingsApplied(AppSettings settings, bool autoStart)
    {
        try
        {
            settings.Save(Path.Combine(_appDirectory, "ui_config.json"));
            WindowsStartup.SetEnabled(autoStart);
            ApplySettings(settings);
            if (_captureSource is not null)
            {
                _captureSource.ResponseReceived -= OnResponseReceived;
                if (_captureSource is CdpCaptureSource oldCdp) oldCdp.StatusChanged -= OnCaptureStatusChanged;
                await _captureSource.DisposeAsync();
                _captureSource = null;
            }
            _settings = settings;
            var newSource = CreateCaptureSource(settings);
            SetCaptureSource(newSource);
            await newSource.StartAsync(CancellationToken.None);
            SetCaptureStatus(settings.CdpEnabled ? "CDP接続待機中" :
                $"74EN JSON監視: {Path.Combine(_appDirectory, "74en_responses")}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or
                                   ArgumentException or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(this, $"設定またはデータ取得元を開始できませんでした。\n{ex.Message}", "設定エラー",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        Topmost = settings.AlwaysOnTop;
        var family = new System.Windows.Media.FontFamily(settings.FontFamily);
        MainTabs.FontFamily = family;
        MainTabs.FontSize = settings.FontSize;
        AdmiralDisplay.FontFamily = family;
        ShipDisplay.FontFamily = family;
        AdmiralDisplay.FontSize = settings.FontSize;
        ShipDisplay.FontSize = settings.FontSize;
        var background = settings.DarkMode ? System.Windows.Media.Color.FromRgb(38, 38, 38) : Colors.White;
        var foreground = settings.DarkMode ? System.Windows.Media.Color.FromRgb(240, 240, 240) : Colors.Black;
        Background = new SolidColorBrush(background);
        Foreground = new SolidColorBrush(foreground);
        MainRoot.Background = new SolidColorBrush(background);
        MainTabs.Background = new SolidColorBrush(background);
        AdmiralDisplay.Foreground = new SolidColorBrush(foreground);
        ShipDisplay.Foreground = new SolidColorBrush(foreground);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitRequested) return;
        e.Cancel = true;
        Hide();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _displayTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        if (_captureSource is not null)
        {
            _captureSource.ResponseReceived -= OnResponseReceived;
            await _captureSource.DisposeAsync();
        }
        System.Windows.Application.Current.Shutdown();
    }
}

