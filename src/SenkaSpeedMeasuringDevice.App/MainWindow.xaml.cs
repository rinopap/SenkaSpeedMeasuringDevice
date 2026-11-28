using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading.Channels;
using Forms = System.Windows.Forms;
using SenkaSpeedMeasuringDevice.Capture;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.App;

public partial class MainWindow : Window
{
    private readonly string _appDirectory = AppContext.BaseDirectory;
    private readonly string _settingsPath;
    private readonly AdmiralObservationStore _admiralStore;
    private readonly List<ExperienceSample> _admiralSamples;
    private readonly List<ExperienceSample> _shipSamples;
    private List<ShipExpGainEvent> _shipGainEvents = [];
    private List<CaptureCoverageSegment> _shipCoverage = [];
    private List<DateTimeOffset> _shipGainIssues = [];
    private readonly object _samplesLock = new();
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _displayTimer;
    private readonly Channel<CaptureItem> _captureQueue = Channel.CreateBounded<CaptureItem>(new BoundedChannelOptions(128)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Task _captureWorker;
    private AppSettings _settings;
    private IReadOnlyList<string> _databaseCandidates = [];
    private ICaptureSource? _captureSource;
    private string? _captureSetupError;
    private bool _exitRequested;
    private long _captureGeneration;
    private long _captureQueuedBytes;
    private EventHandler<ApiResponse>? _responseHandler;
    private string? _activeAdmiralSeriesPrefix;
    private string? _pendingJsonSeriesMigration;
    private string _captureStatusDetails = "取得元を準備中";

    public MainWindow()
    {
        InitializeComponent();
        _captureWorker = ProcessCaptureQueueAsync();
        _settingsPath = Path.Combine(_appDirectory, "ui_config.json");
        _settings = AppSettings.Load(_settingsPath);
        _pendingJsonSeriesMigration = _settings.AdmiralPendingSeriesMigrationFrom;
        _databaseCandidates = ElectronicObserverDatabaseDiscovery.FindValidDatabases(_settings.SqliteDatabasePath);
        if (string.IsNullOrWhiteSpace(_settings.SqliteDatabasePath) &&
            !_settings.CaptureModeNeedsUserConfirmation && _settings.EffectiveCaptureMode == "sqlite" &&
            _databaseCandidates.Count == 1)
            _settings = _settings with { SqliteDatabasePath = _databaseCandidates[0] };
        if (_settings.CaptureModeNeedsUserConfirmation)
            _captureSetupError = "以前のCDP設定を停止しました。設定からSQLiteまたはJSON取得を選択してください";
        else if (_settings.EffectiveCaptureMode == "sqlite" && string.IsNullOrWhiteSpace(_settings.SqliteDatabasePath))
            _captureSetupError = _databaseCandidates.Count > 1
                ? "74EN DB候補が複数あります。設定から使用するDBを選択してください"
                : "74EN DBが見つかりません。74ENを起動するか、設定からDBを選択してください";
        var admiralCsvPath = Path.Combine(_appDirectory, "exp_log.csv");
        var stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SenkaSpeedMeasuringDevice");
        _admiralStore = new AdmiralObservationStore(Path.Combine(stateDirectory, "admiral-observations.sqlite"));
        try
        {
            _admiralStore.ImportLegacyCsvOnce(CsvStorage.Load(admiralCsvPath, "admiral_exp"), admiralCsvPath);
            _admiralStore.ExportAdmiralCsv(admiralCsvPath);
            _admiralStore.ExportSqliteAdmiralCsv(Path.Combine(_appDirectory, "exp_log_sqlite.csv"));
            _admiralStore.ExportShipExpGainCsv(Path.Combine(_appDirectory, "ship_exp_gain_log.csv"));
            var initialAdmiralSeries = _settings.AdmiralContinuitySeriesId ??
                (_settings.EffectiveMeasurementMode == "json" ? "json" : null);
            _admiralSamples = _admiralStore.LoadAdmiralSamples(initialAdmiralSeries).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _captureSetupError = "既存exp_log.csvを保全しました。形式または保存先を確認してください";
            _admiralSamples = CsvStorage.Load(admiralCsvPath, "admiral_exp").ToList();
        }
        var shipCsvPath = Path.Combine(_appDirectory, "ship_exp_log.csv");
        var jsonShipSeries = _settings.JsonExperienceSeriesId ?? "json";
        try
        {
            _admiralStore.ImportLegacyJsonShipCsvOnce(shipCsvPath, jsonShipSeries);
            _admiralStore.ExportJsonShipCsv(shipCsvPath);
            _shipSamples = _admiralStore.LoadJsonShipSamples(jsonShipSeries).ToList();
            if (_settings.EffectiveCaptureMode == "off" && _settings.EffectiveMeasurementMode == "sqlite" &&
                _settings.AdmiralContinuitySeriesId is { Length: > 0 } previousSqliteSeries)
            {
                _shipGainEvents = _admiralStore.LoadShipExpGainEvents(previousSqliteSeries).ToList();
                _shipCoverage = _admiralStore.LoadCoverageSegments(previousSqliteSeries).ToList();
                _shipGainIssues = _admiralStore.LoadShipExpGainIssueTimes(previousSqliteSeries).ToList();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            _captureSetupError ??= "既存ship_exp_log.csvを保全しました。形式または保存先を確認してください";
            _shipSamples = CsvStorage.Load(shipCsvPath, "ship_total_exp").ToList();
        }
        try
        {
            var initialSource = CaptureStartPolicy.ShouldStartOnApplicationLaunch(_settings.EffectiveCaptureMode)
                ? CreateCaptureSource(_settings)
                : null;
            if (initialSource is not null) SetCaptureSource(initialSource);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _captureSetupError = "選択された取得先を開始できません。設定から取得先を確認してください";
        }

        _trayIcon = CreateTrayIcon();
        _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _displayTimer.Tick += (_, _) => RefreshDisplays();
        _displayTimer.Start();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        ApplySettings(_settings);
        RefreshDisplays();
        Loaded += (_, _) => UpdateMinimumWindowSize();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_captureSource is null)
        {
            SetCaptureStatus(_captureSetupError is not null
                ? (_settings.CaptureModeNeedsUserConfirmation ? "設定移行が必要: " : "取得先を確認してください: ") + _captureSetupError
                : _settings.EffectiveCaptureMode == "sqlite"
                    ? "74EN DB待機中: 設定を確認してください"
                    : "監視停止中");
            return;
        }
        try
        {
            await _captureSource.StartAsync(CancellationToken.None);
            if (_captureSource is FileCaptureSource)
                SetCaptureStatus($"74EN JSON監視: {_settings.JsonResponseDirectory ?? Path.Combine(_appDirectory, "74en_responses")}");
            else if (_captureSource is SqliteCaptureSource)
                SetCaptureStatus("SQLite監視中: 履歴を確認しています");
            if (_captureSetupError is not null) SetCaptureStatus(_captureSetupError);
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
        _activeAdmiralSeriesPrefix = source is SqliteCaptureSource sqliteSource &&
                                     !string.IsNullOrWhiteSpace(sqliteSource.MeasurementSeriesId)
            ? sqliteSource.MeasurementSeriesId
            : source is FileCaptureSource
                ? _settings.AdmiralContinuitySeriesId ?? "json"
                : _settings.AdmiralContinuitySeriesId;
        if (_activeAdmiralSeriesPrefix is { Length: > 0 })
        {
            lock (_samplesLock)
            {
                _admiralSamples.Clear();
                _admiralSamples.AddRange(_admiralStore.LoadAdmiralSamples(_activeAdmiralSeriesPrefix));
                if (source is SqliteCaptureSource activeSqlite)
                {
                    var activeSeries = activeSqlite.MeasurementSeriesId;
                    _shipGainEvents = _admiralStore.LoadShipExpGainEvents(activeSeries).ToList();
                    _shipCoverage = _admiralStore.LoadCoverageSegments(activeSeries).ToList();
                    _shipGainIssues = _admiralStore.LoadShipExpGainIssueTimes(activeSeries).ToList();
                }
            }
        }
        if (source is FileCaptureSource)
        {
            var jsonShipSeries = _settings.JsonExperienceSeriesId ?? "json";
            lock (_samplesLock)
            {
                _shipSamples.Clear();
                _shipSamples.AddRange(_admiralStore.LoadJsonShipSamples(jsonShipSeries));
            }
        }
        var generation = AdvanceCaptureGeneration();
        _responseHandler = (_, response) =>
        {
            var byteCount = response.ResponseBody.Length;
            if (byteCount > 8 * 1024 * 1024 || Interlocked.Add(ref _captureQueuedBytes, byteCount) > 16 * 1024 * 1024)
            {
                Interlocked.Add(ref _captureQueuedBytes, -byteCount);
                Dispatcher.InvokeAsync(() => SetCaptureStatus("取得キュー超過: 計測データを破棄しました"));
                return;
            }
            if (!_captureQueue.Writer.TryWrite(new CaptureItem(generation, response, byteCount)))
            {
                Interlocked.Add(ref _captureQueuedBytes, -byteCount);
                Dispatcher.InvokeAsync(() => SetCaptureStatus("取得キュー超過: 計測データを破棄しました"));
            }
        };
        source.ResponseReceived += _responseHandler;
        if (source is SqliteCaptureSource sqlite)
        {
            sqlite.StatusChanged += OnCaptureStatusChanged;
            sqlite.MeasurementSeriesChanged += OnMeasurementSeriesChanged;
            sqlite.PortBacklogSkipped += OnPortBacklogSkipped;
            if (_settings.SqlitePortBacklogSkipToken is { Length: > 0 } skipToken)
                sqlite.SkipPortBacklogOnNextStart(skipToken);
        }
    }

    private void OnPortBacklogSkipped(object? sender, EventArgs e)
    {
        if (sender is not SqliteCaptureSource sqlite) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_captureSource, sqlite)) return;
            _settings = _settings with { SqlitePortBacklogSkipToken = null };
            try { _settings.Save(_settingsPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                SetCaptureStatus("設定保存エラー: 方式切替境界の完了状態を保存できません");
            }
        });
    }

    private void OnMeasurementSeriesChanged(object? sender, string seriesId)
    {
        if (sender is not SqliteCaptureSource sqlite) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_captureSource, sqlite)) return;
            _activeAdmiralSeriesPrefix = seriesId;
            if (_pendingJsonSeriesMigration is { Length: > 0 } previousJsonSeries)
            {
                if (!string.Equals(previousJsonSeries, seriesId, StringComparison.Ordinal))
                {
                    try { _admiralStore.MoveAdmiralObservationsToSeries(previousJsonSeries, seriesId); }
                    catch (Microsoft.Data.Sqlite.SqliteException)
                    {
                        SetCaptureStatus("履歴引継ぎエラー: JSON由来の戦果履歴を保全しています");
                        return;
                    }
                }
                _pendingJsonSeriesMigration = null;
                _settings = _settings with { AdmiralPendingSeriesMigrationFrom = null };
            }
            lock (_samplesLock)
            {
                _admiralSamples.Clear();
                _admiralSamples.AddRange(_admiralStore.LoadAdmiralSamples(seriesId));
                _shipGainEvents = _admiralStore.LoadShipExpGainEvents(seriesId).ToList();
                _shipCoverage = _admiralStore.LoadCoverageSegments(seriesId).ToList();
                _shipGainIssues = _admiralStore.LoadShipExpGainIssueTimes(seriesId).ToList();
            }
            PersistAdmiralSeries(seriesId);
            RefreshDisplays();
        });
    }

    private ICaptureSource? CreateCaptureSource(AppSettings settings)
    {
        if (settings.EffectiveCaptureMode == "off") return null;
        if (settings.EffectiveCaptureMode == "json")
            return new FileCaptureSource(settings.JsonResponseDirectory ?? Path.Combine(_appDirectory, "74en_responses"));
        if (settings.EffectiveCaptureMode == "sqlite")
        {
            if (string.IsNullOrWhiteSpace(settings.SqliteDatabasePath))
                throw new ArgumentException("74ENのElectronicObserver.sqliteを設定してください。");
            if (!ElectronicObserverDatabaseDiscovery.IsLocalFile(settings.SqliteDatabasePath))
                throw new ArgumentException("74EN SQLiteはローカルのファイルを選択してください。");
            if (ElectronicObserverDatabaseDiscovery.AreSameFile(settings.SqliteDatabasePath, _admiralStore.DatabasePath))
                throw new ArgumentException("計測機専用SQLiteと74ENのデータベースに同じファイルは指定できません。");
            new SqliteApiFileReader(settings.SqliteDatabasePath).ValidateDatabase();
            return new SqliteCaptureSource(settings.SqliteDatabasePath, observationStore: _admiralStore);
        }
        return null;
    }

    private void OnCaptureStatusChanged(object? sender, string status) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (sender is SqliteCaptureSource sqlite)
            {
                var prefix = sqlite.MeasurementSeriesId;
                var exportGains = false;
                if (!string.Equals(prefix, _activeAdmiralSeriesPrefix, StringComparison.Ordinal))
                {
                    _activeAdmiralSeriesPrefix = prefix;
                    lock (_samplesLock)
                    {
                        _admiralSamples.Clear();
                        _admiralSamples.AddRange(_admiralStore.LoadAdmiralSamples(prefix));
                        _shipGainEvents = _admiralStore.LoadShipExpGainEvents(sqlite.MeasurementSeriesId).ToList();
                        _shipCoverage = _admiralStore.LoadCoverageSegments(sqlite.MeasurementSeriesId).ToList();
                        _shipGainIssues = _admiralStore.LoadShipExpGainIssueTimes(sqlite.MeasurementSeriesId).ToList();
                    }
                    exportGains = true;
                    RefreshDisplays();
                }
                else
                {
                    lock (_samplesLock)
                    {
                        var current = _admiralStore.LoadShipExpGainEvents(sqlite.MeasurementSeriesId).ToList();
                        exportGains = current.Count != _shipGainEvents.Count;
                        _shipGainEvents = current;
                        _shipCoverage = _admiralStore.LoadCoverageSegments(sqlite.MeasurementSeriesId).ToList();
                        _shipGainIssues = _admiralStore.LoadShipExpGainIssueTimes(sqlite.MeasurementSeriesId).ToList();
                    }
                }
                if (exportGains)
                {
                    try { _admiralStore.ExportShipExpGainCsv(Path.Combine(_appDirectory, "ship_exp_gain_log.csv")); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                    {
                        status = "CSV書き込みエラー: 艦娘獲得経験値CSVを確認してください";
                    }
                }
                RefreshDisplays();
            }
            SetCaptureStatus(status);
        });

    private async Task ProcessCaptureQueueAsync()
    {
        await foreach (var item in _captureQueue.Reader.ReadAllAsync())
        {
            Interlocked.Add(ref _captureQueuedBytes, -item.ByteCount);
            var sqliteSource = _settings.EffectiveCaptureMode == "sqlite";
            var snapshot = sqliteSource
                ? ApiResponseParser.ParseAdmiralPort(item.Response)
                : ApiResponseParser.Parse(item.Response);
            if (snapshot is null)
            {
                if (sqliteSource && ShipExpGainParser.Parse(item.Response).Event is not null &&
                    _captureSource is SqliteCaptureSource sqlite &&
                    item.Response.MeasurementSeriesId is { } responseSeries &&
                    (string.Equals(responseSeries, sqlite.MeasurementSeriesId, StringComparison.Ordinal) ||
                     responseSeries.StartsWith(sqlite.MeasurementSeriesId + ":", StringComparison.Ordinal)))
                {
                    var shipStorageFailed = false;
                    lock (_samplesLock)
                    {
                        if (item.Generation != Volatile.Read(ref _captureGeneration)) continue;
                        try
                        {
                            _shipGainEvents = _admiralStore.LoadShipExpGainEvents(sqlite.MeasurementSeriesId).ToList();
                            _shipCoverage = _admiralStore.LoadCoverageSegments(sqlite.MeasurementSeriesId).ToList();
                            _shipGainIssues = _admiralStore.LoadShipExpGainIssueTimes(sqlite.MeasurementSeriesId).ToList();
                            _admiralStore.ExportShipExpGainCsv(Path.Combine(_appDirectory, "ship_exp_gain_log.csv"));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
                        {
                            shipStorageFailed = true;
                        }
                    }
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (item.Generation != Volatile.Read(ref _captureGeneration)) return;
                        SetCaptureStatus(shipStorageFailed ? "CSV書き込みエラー: データフォルダを確認してください" :
                            "SQLite監視中：艦娘API獲得経験値を集計しています");
                        RefreshDisplays();
                    });
                    continue;
                }
                continue;
            }
            var storageFailed = false;
            lock (_samplesLock)
            {
                if (item.Generation != Volatile.Read(ref _captureGeneration)) continue;
                if (!sqliteSource)
                {
                    try
                    {
                        var jsonSeries = _activeAdmiralSeriesPrefix ??
                                         _settings.AdmiralContinuitySeriesId ?? "json";
                        var jsonShipSeries = _settings.JsonExperienceSeriesId ?? "json";
                        _activeAdmiralSeriesPrefix = jsonSeries;
                        var record = _admiralStore.RecordJsonPortObservation(item.Response.EventId,
                            snapshot.AdmiralExperience is { } admiral
                                ? new ExperienceSample(snapshot.Timestamp, admiral) : null,
                            jsonSeries,
                            snapshot.ShipTotalExperience is { } shipTotal
                                ? new ExperienceSample(snapshot.Timestamp, shipTotal) : null,
                            jsonShipSeries);
                        if (record.AdmiralAdded)
                        {
                            _admiralStore.ExportAdmiralCsv(Path.Combine(_appDirectory, "exp_log.csv"));
                            _admiralSamples.Clear();
                            _admiralSamples.AddRange(_admiralStore.LoadAdmiralSamples(jsonSeries));
                        }
                        if (record.ShipAdded)
                        {
                            _admiralStore.ExportJsonShipCsv(Path.Combine(_appDirectory, "ship_exp_log.csv"));
                            _shipSamples.Clear();
                            _shipSamples.AddRange(_admiralStore.LoadJsonShipSamples(jsonShipSeries));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { storageFailed = true; }
                }
                else if (snapshot.AdmiralExperience is not null)
                {
                    try
                    {
                        if (_captureSource is SqliteCaptureSource currentSqlite)
                            _activeAdmiralSeriesPrefix = currentSqlite.MeasurementSeriesId;
                        _admiralStore.ExportSqliteAdmiralCsv(Path.Combine(_appDirectory, "exp_log_sqlite.csv"));
                        var currentSeries = _captureSource is SqliteCaptureSource activeSqlite
                            ? activeSqlite.MeasurementSeriesId : string.Empty;
                        _shipGainEvents = _admiralStore.LoadShipExpGainEvents(currentSeries).ToList();
                        _shipCoverage = _admiralStore.LoadCoverageSegments(currentSeries).ToList();
                        _shipGainIssues = _admiralStore.LoadShipExpGainIssueTimes(currentSeries).ToList();
                        _admiralStore.ExportShipExpGainCsv(Path.Combine(_appDirectory, "ship_exp_gain_log.csv"));
                        _admiralSamples.Clear();
                        _admiralSamples.AddRange(_admiralStore.LoadAdmiralSamples(_activeAdmiralSeriesPrefix));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { storageFailed = true; }
                }
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (item.Generation != Volatile.Read(ref _captureGeneration)) return;
                SetCaptureStatus(storageFailed ? "CSV書き込みエラー: データフォルダを確認してください" :
                    _settings.EffectiveCaptureMode == "sqlite" ? "SQLite監視中：母港経験値・結果APIを計測中" :
                    "74EN JSON監視: 計測中");
                RefreshDisplays();
            });
        }
    }

    private void SetCaptureStatus(string details)
    {
        _captureStatusDetails = details;
        if (MainTabs.Template.FindName("CaptureStatus", MainTabs) is not System.Windows.Controls.TextBlock status)
            return;

        status.Text = details switch
        {
            _ when details.StartsWith("74EN JSON監視", StringComparison.Ordinal) => "74EN JSON監視",
            _ when details.StartsWith("SQLite監視中", StringComparison.Ordinal) => "SQLite監視中",
            _ when details.StartsWith("SQLite初回履歴", StringComparison.Ordinal) => "履歴取得中",
            _ when details.StartsWith("SQLite読み取り", StringComparison.Ordinal) => "DB読取エラー",
            _ when details.StartsWith("74EN停止", StringComparison.Ordinal) => "74EN停止中",
            _ when details.StartsWith("SQLite接続中", StringComparison.Ordinal) => "API待機中",
            _ when details.StartsWith("対応範囲外", StringComparison.Ordinal) => "対象外イベント",
            _ when details.StartsWith("履歴不足", StringComparison.Ordinal) => "履歴不足",
            _ when details.StartsWith("設定移行が必要", StringComparison.Ordinal) => "設定移行が必要",
            _ when details.StartsWith("取得先を確認", StringComparison.Ordinal) => "取得先エラー",
            _ when details.StartsWith("対象イベント待機", StringComparison.Ordinal) => "イベント待機中",
            _ when details.StartsWith("CSV書き込みエラー", StringComparison.Ordinal) => "CSVエラー",
            _ when details.Contains("開始できません", StringComparison.Ordinal) => "取得エラー",
            _ => details
        };
        status.ToolTip = details;
        var tone = details.StartsWith("CSV書き込みエラー", StringComparison.Ordinal) ||
                   details.StartsWith("DB読取エラー", StringComparison.Ordinal) ||
                   details.StartsWith("取得先を確認", StringComparison.Ordinal) ||
                   details.Contains("開始できません", StringComparison.Ordinal) ||
                   details.StartsWith("履歴引継ぎエラー", StringComparison.Ordinal) ||
                   details.StartsWith("設定保存エラー", StringComparison.Ordinal)
            ? CaptureStatusTone.Error
            : details.StartsWith("対応範囲外", StringComparison.Ordinal) ||
              details.StartsWith("履歴不足", StringComparison.Ordinal) ||
              details.Contains("部分計測", StringComparison.Ordinal) ||
              details.StartsWith("取得キュー超過", StringComparison.Ordinal) ||
              details.StartsWith("設定移行が必要", StringComparison.Ordinal)
                ? CaptureStatusTone.Warning
                : details.Contains("待機", StringComparison.Ordinal) ||
                  details.Contains("確認中", StringComparison.Ordinal) ||
                  details.StartsWith("履歴取得中", StringComparison.Ordinal)
                    ? CaptureStatusTone.Info
                    : CaptureStatusTone.Normal;
        var hex = CaptureStatusPalette.ForegroundHex(_settings.DarkMode, tone);
        status.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
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
        if (_settings.EffectiveMeasurementMode == "sqlite")
        {
            var uncertainIssueWindows = _activeAdmiralSeriesPrefix is { Length: > 0 } series
                ? _admiralStore.LoadUnknownShipExpGainIssueWindows(series)
                : [];
            var buckets = ShipExpGainRateCalculator.CalculateThreeHours(_shipGainEvents, _shipCoverage, now,
                _shipGainIssues, uncertainIssueWindows);
            ShipDisplay.Text = ShipExpGainRateCalculator.FormatBuckets(buckets);
            ShipDisplay.ToolTip = ShipExpGainRateCalculator.FormatCoverageDiagnostics(buckets) ??
                "計測区間の状態: 直近3時間を表示しています。";
        }
        else
        {
            ShipDisplay.Text = SenkaCalculator.FormatThreeHours(ships, now, ship: true);
            ShipDisplay.ToolTip = "JSON方式: 全所持艦の累積経験値差分です。SQLite方式のAPI獲得経験値とは指標が異なります。";
        }
        UpdateMinimumWindowSize();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        _databaseCandidates = ElectronicObserverDatabaseDiscovery.FindValidDatabases(_settings.SqliteDatabasePath);
        var dialog = new SettingsWindow(_settings, WindowsStartup.IsEnabled(), _databaseCandidates,
            Path.Combine(_appDirectory, "74en_responses")) { Owner = this };
        dialog.SettingsApplied += OnSettingsApplied;
        dialog.NewMeasurementSessionRequested += async () =>
        {
            if (_captureSource is not SqliteCaptureSource sqlite)
            {
                SetCaptureStatus("計測区間を開始できません: SQLite監視を選択してください");
                return;
            }
            try
            {
                await sqlite.BeginNewMeasurementSessionAsync(CancellationToken.None);
                SetCaptureStatus("新しい計測区間を開始しました");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                SetCaptureStatus("計測区間を開始できませんでした: DB状態を確認してください");
            }
        };
        dialog.ShowDialog();
    }

    private async void OnSettingsApplied(AppSettings settings, bool autoStart, bool continueMeasurementSeries)
    {
        var previousSettings = _settings;
        var previousSource = _captureSource;
        var previousMode = previousSettings.EffectiveCaptureMode;
        var targetMode = settings.EffectiveCaptureMode;
        var activeSeries = _activeAdmiralSeriesPrefix ?? previousSettings.AdmiralContinuitySeriesId;
        if (targetMode != "off")
        {
            settings = settings with
            {
                AdmiralContinuitySeriesId = continueMeasurementSeries
                    ? activeSeries ?? settings.AdmiralContinuitySeriesId
                    : settings.AdmiralContinuitySeriesId
            };
        }
        var beginNewSqliteSession = targetMode == "sqlite" && previousMode != "sqlite" &&
                                    !continueMeasurementSeries;
        var pendingSeriesMigration = targetMode == "sqlite" && previousMode == "json" &&
                                     continueMeasurementSeries ? activeSeries : null;
        _pendingJsonSeriesMigration = pendingSeriesMigration;
        settings = settings with
        {
            AdmiralPendingSeriesMigrationFrom = pendingSeriesMigration,
            SqlitePortBacklogSkipToken = targetMode == "sqlite" && previousMode == "json" && continueMeasurementSeries
                ? Guid.NewGuid().ToString("N") : null
        };
        ICaptureSource? newSource = null;
        try
        {
            if (targetMode == "sqlite" && settings.SqliteDatabasePath is { } databasePath)
                new SqliteApiFileReader(databasePath).ValidateDatabase();
            var shouldStartSource = CaptureStartPolicy.ShouldStartAfterSettingsApplied(targetMode, explicitStartRequest: false);
            newSource = shouldStartSource ? CreateCaptureSource(settings) : null;
            if (_captureSource is not null)
            {
                AdvanceCaptureGeneration();
                if (_responseHandler is not null) _captureSource.ResponseReceived -= _responseHandler;
                if (_captureSource is SqliteCaptureSource oldSqlite) oldSqlite.StatusChanged -= OnCaptureStatusChanged;
                if (_captureSource is SqliteCaptureSource oldSqliteSeries) oldSqliteSeries.MeasurementSeriesChanged -= OnMeasurementSeriesChanged;
                if (_captureSource is SqliteCaptureSource oldSqliteBacklog) oldSqliteBacklog.PortBacklogSkipped -= OnPortBacklogSkipped;
                await _captureSource.DisposeAsync();
                _captureSource = null;
            }
            _settings = settings with { CaptureModeNeedsUserConfirmation = false };
            if (targetMode == "sqlite" && string.IsNullOrWhiteSpace(_settings.AdmiralContinuitySeriesId))
            {
                lock (_samplesLock)
                {
                    _admiralSamples.Clear();
                    _shipGainEvents.Clear();
                    _shipCoverage.Clear();
                    _shipGainIssues.Clear();
                }
            }
            ApplySettings(_settings);
            _databaseCandidates = ElectronicObserverDatabaseDiscovery.FindValidDatabases(settings.SqliteDatabasePath);
            if (newSource is not null)
            {
                SetCaptureSource(newSource);
                await newSource.StartAsync(CancellationToken.None);
                if (beginNewSqliteSession && newSource is SqliteCaptureSource sqlite)
                    await sqlite.BeginNewMeasurementSessionAsync(CancellationToken.None);
            }
            if (targetMode == "json" && _activeAdmiralSeriesPrefix is { Length: > 0 } jsonSeries)
                _settings = _settings with { AdmiralContinuitySeriesId = jsonSeries };
            _settings.Save(_settingsPath);
            SetCaptureStatus(targetMode switch
            {
                "json" => $"74EN JSON監視: {settings.JsonResponseDirectory ?? Path.Combine(_appDirectory, "74en_responses")} (観測待ち)",
                "sqlite" => "SQLite接続待機中: API獲得経験値履歴を確認しています",
                _ => "監視停止中"
            });
            RefreshDisplays();
            try { WindowsStartup.SetEnabled(autoStart); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                System.Windows.MessageBox.Show(this, "取得設定は適用しましたが、自動起動の設定を保存できませんでした。",
                    "自動起動", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or
                                   ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (newSource is not null)
            {
                if (ReferenceEquals(_captureSource, newSource))
                {
                    AdvanceCaptureGeneration();
                    if (_responseHandler is not null) newSource.ResponseReceived -= _responseHandler;
                    if (newSource is SqliteCaptureSource failedSqlite)
                    {
                        failedSqlite.StatusChanged -= OnCaptureStatusChanged;
                        failedSqlite.MeasurementSeriesChanged -= OnMeasurementSeriesChanged;
                        failedSqlite.PortBacklogSkipped -= OnPortBacklogSkipped;
                    }
                    _captureSource = null;
                }
                try { await newSource.DisposeAsync(); } catch { }
            }
            _settings = previousSettings;
            ApplySettings(_settings);
            try
            {
                if (!ReferenceEquals(_captureSource, previousSource))
                {
                    var restoredSource = CaptureStartPolicy.ShouldStartOnApplicationLaunch(previousMode)
                        ? CreateCaptureSource(previousSettings)
                        : null;
                    if (restoredSource is not null)
                    {
                        SetCaptureSource(restoredSource);
                        await restoredSource.StartAsync(CancellationToken.None);
                    }
                    else _captureSource = null;
                }
            }
            catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                SetCaptureStatus("設定変更失敗後に取得元を復元できませんでした");
            }
            _pendingJsonSeriesMigration = null;
            RefreshDisplays();
            System.Windows.MessageBox.Show(this, $"設定またはデータ取得元を開始できませんでした。\n{ex.Message}", "設定エラー",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        Topmost = settings.AlwaysOnTop;
        var family = new System.Windows.Media.FontFamily(settings.FontFamily);
        MainTabs.FontFamily = family;
        MainTabs.FontSize = UiFontSizing.QtPointSizeToWpfDip(settings.FontSize);
        AdmiralDisplay.FontFamily = family;
        ShipDisplay.FontFamily = family;
        AdmiralDisplay.FontSize = UiFontSizing.DisplayTextDip(settings.FontSize);
        ShipDisplay.FontSize = UiFontSizing.DisplayTextDip(settings.FontSize);
        var palette = UiThemePalette.For(settings.DarkMode);
        if (System.Windows.Application.Current is { } app)
        {
            app.Resources["AppBackgroundBrush"] = Brush(palette.Background);
            app.Resources["AppSurfaceBrush"] = Brush(palette.Surface);
            app.Resources["AppForegroundBrush"] = Brush(palette.Foreground);
            app.Resources["AppMutedForegroundBrush"] = Brush(palette.MutedForeground);
            app.Resources["AppBorderBrush"] = Brush(palette.Border);
            app.Resources["AppSelectedTabBrush"] = Brush(palette.SelectedTab);
            app.Resources["AppSelectedTabForegroundBrush"] = Brush(palette.SelectedTabForeground);
            app.Resources["AppHoverBrush"] = Brush(palette.Hover);
            app.Resources["AppFocusBrush"] = Brush(palette.Focus);
        }
        Background = Brush(palette.Background);
        Foreground = Brush(palette.Foreground);
        MainRoot.Background = Brush(palette.Background);
        MainTabs.Background = Brush(palette.Background);
        MainTabs.Foreground = Brush(palette.Foreground);
        AdmiralDisplay.Foreground = Brush(palette.Foreground);
        ShipDisplay.Foreground = Brush(palette.Foreground);
        UpdateMinimumWindowSize(fitToContent: true);
        SetCaptureStatus(_captureStatusDetails);
    }

    private static SolidColorBrush Brush(string value) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));

    private void UpdateMinimumWindowSize(bool fitToContent = false)
    {
        if (!IsInitialized || MainTabs is null) return;
        var fontFamily = new System.Windows.Media.FontFamily(_settings.FontFamily);
        var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var displayFontSize = UiFontSizing.DisplayTextDip(_settings.FontSize);
        var displayLines = new[]
        {
            "・00:00~01:00 戦果時速 999.99",
            UiFontSizing.MaximumExpRateWidthSample,
            "・00:00~01:00 Exp時速 未確定",
            "データなし"
        };
        var maxTextWidth = displayLines.Select(text => new FormattedText(text, CultureInfo.GetCultureInfo("ja-JP"),
            System.Windows.FlowDirection.LeftToRight, typeface, displayFontSize, System.Windows.Media.Brushes.Black, dpi).WidthIncludingTrailingWhitespace)
            .Max();
        var tabTextWidth = new[] { "戦果時速", "Exp時速" }.Select(text =>
            new FormattedText(text, CultureInfo.GetCultureInfo("ja-JP"), System.Windows.FlowDirection.LeftToRight, typeface,
                MainTabs.FontSize, System.Windows.Media.Brushes.Black, dpi).WidthIncludingTrailingWhitespace).Max();
        var headerWidth = tabTextWidth * 2 + 100 + 28 + 64;
        var requiredWidth = Math.Max(340, Math.Ceiling(Math.Max(maxTextWidth + 48, headerWidth)));
        var requiredHeight = Math.Max(156, Math.Ceiling(displayFontSize * 3.5 + 85));
        MinWidth = requiredWidth;
        MinHeight = requiredHeight;
        if (Width < MinWidth) Width = MinWidth;
        if (Height < MinHeight) Height = MinHeight;
        if (fitToContent) Width = MinWidth;
    }

    private void PersistAdmiralSeries(string seriesId)
    {
        if (string.Equals(_settings.AdmiralContinuitySeriesId, seriesId, StringComparison.Ordinal) &&
            _settings.AdmiralPendingSeriesMigrationFrom is null) return;
        _settings = _settings with
        {
            AdmiralContinuitySeriesId = seriesId,
            AdmiralPendingSeriesMigrationFrom = null
        };
        try { _settings.Save(_settingsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SetCaptureStatus("設定保存エラー: 計測系列を次回起動へ保存できません");
        }
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
            AdvanceCaptureGeneration();
            if (_responseHandler is not null) _captureSource.ResponseReceived -= _responseHandler;
            if (_captureSource is SqliteCaptureSource oldSqlite) oldSqlite.StatusChanged -= OnCaptureStatusChanged;
            if (_captureSource is SqliteCaptureSource oldSqliteSeries) oldSqliteSeries.MeasurementSeriesChanged -= OnMeasurementSeriesChanged;
            if (_captureSource is SqliteCaptureSource oldSqliteBacklog) oldSqliteBacklog.PortBacklogSkipped -= OnPortBacklogSkipped;
            await _captureSource.DisposeAsync();
        }
        _captureQueue.Writer.TryComplete();
        try { await _captureWorker; } catch (OperationCanceledException) { }
        System.Windows.Application.Current.Shutdown();
    }

    private sealed record CaptureItem(long Generation, ApiResponse Response, int ByteCount);

    private long AdvanceCaptureGeneration()
    {
        lock (_samplesLock) return Interlocked.Increment(ref _captureGeneration);
    }
}
