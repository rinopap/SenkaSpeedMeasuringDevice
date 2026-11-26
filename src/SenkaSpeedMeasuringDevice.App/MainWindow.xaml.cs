using System.IO;
using System.Windows;
using SenkaSpeedMeasuringDevice.Capture;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.App;

public partial class MainWindow : Window
{
    private readonly string _appDirectory = AppContext.BaseDirectory;
    private readonly List<ExperienceSample> _admiralSamples;
    private readonly List<ExperienceSample> _shipSamples;
    private readonly object _samplesLock = new();
    private readonly FileCaptureSource _captureSource;

    public MainWindow()
    {
        InitializeComponent();
        _admiralSamples = CsvStorage.Load(Path.Combine(_appDirectory, "exp_log.csv"), "admiral_exp").ToList();
        _shipSamples = CsvStorage.Load(Path.Combine(_appDirectory, "ship_exp_log.csv"), "ship_total_exp").ToList();
        _captureSource = new FileCaptureSource(Path.Combine(_appDirectory, "74en_responses"));
        _captureSource.ResponseReceived += OnResponseReceived;
        Loaded += OnLoaded;
        Closed += OnClosed;
        RefreshDisplays();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _captureSource.StartAsync(CancellationToken.None);
            CaptureStatus.Text = $"74EN JSON監視: {_appDirectory}74en_responses";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CaptureStatus.Text = "74EN JSON監視を開始できません";
        }
    }

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
            CaptureStatus.Text = storageFailed ? "CSV書き込みエラー: データフォルダを確認してください" : "74EN JSON監視: 計測中";
            RefreshDisplays();
        });
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

    private async void OnClosed(object? sender, EventArgs e) => await _captureSource.DisposeAsync();
}

