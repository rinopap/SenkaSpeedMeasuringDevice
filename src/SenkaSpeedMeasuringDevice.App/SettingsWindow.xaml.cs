using System.IO;
using System.Windows;
using SenkaSpeedMeasuringDevice.Capture;
using SenkaSpeedMeasuringDevice.Core;
using Forms = System.Windows.Forms;

namespace SenkaSpeedMeasuringDevice.App;

public partial class SettingsWindow : Window
{
    private const string SettingsUiFontFamily = "メイリオ";
    private const int SettingsUiFontPointSize = 10;
    private readonly AppSettings _settings;

    public event Action<AppSettings, bool, bool>? SettingsApplied;
    public event Action? NewMeasurementSessionRequested;

    public SettingsWindow(AppSettings settings, bool autoStart,
        IReadOnlyList<string> detectedDatabasePaths, string defaultJsonDirectory)
    {
        InitializeComponent();
        _settings = settings;
        FontFamily = new System.Windows.Media.FontFamily(SettingsUiFontFamily);
        FontSize = UiFontSizing.QtPointSizeToWpfDip(SettingsUiFontPointSize);
        FontFamilyBox.ItemsSource = new[] { "メイリオ", "Yu Gothic", "MS Gothic", "Arial", "Courier New" };
        CaptureModeBox.ItemsSource = new[]
        {
            new CaptureModeOption("SQLite（推奨）", "sqlite"),
            new CaptureModeOption("74EN JSONファイル", "json"),
            new CaptureModeOption("停止", "off")
        };
        CaptureModeBox.DisplayMemberPath = nameof(CaptureModeOption.Label);
        CaptureModeBox.SelectedValuePath = nameof(CaptureModeOption.Value);
        CaptureModeBox.SelectedValue = settings.EffectiveCaptureMode;

        var databaseCandidates = detectedDatabasePaths.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new DatabaseCandidate(Path.GetFullPath(path))).ToList();
        DetectedDatabaseBox.ItemsSource = databaseCandidates;
        if (settings.SqliteDatabasePath is { Length: > 0 } configuredPath)
        {
            var selected = databaseCandidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Path, configuredPath, StringComparison.OrdinalIgnoreCase));
            DetectedDatabaseBox.SelectedItem = selected;
            SqliteDatabasePathBox.Text = configuredPath;
        }
        else if (databaseCandidates.Count == 1)
        {
            DetectedDatabaseBox.SelectedIndex = 0;
            SqliteDatabasePathBox.Text = databaseCandidates[0].Path;
        }
        JsonResponseDirectoryBox.Text = settings.JsonResponseDirectory ?? defaultJsonDirectory;

        FontFamilyBox.SelectedItem = FontFamilyBox.Items.Contains(settings.FontFamily) ? settings.FontFamily : "Yu Gothic";
        DarkModeCheck.IsChecked = settings.DarkMode;
        FontSizeSlider.Value = Math.Clamp(settings.FontSize, 8, 24);
        AlwaysOnTopCheck.IsChecked = settings.AlwaysOnTop;
        AutoStartCheck.IsChecked = autoStart;
        MigrationNotice.Text = settings.CaptureModeNeedsUserConfirmation
            ? "以前のCDP設定は74EN専用版では使用しません。安全のため取得を停止しています。SQLiteまたはJSONを選び、内容を確認して適用してください。"
            : string.Empty;
        MigrationNotice.Visibility = settings.CaptureModeNeedsUserConfirmation
            ? Visibility.Visible : Visibility.Collapsed;
        FontSizeValue.Text = $"{(int)FontSizeSlider.Value} pt";
        FontSizeSlider.ValueChanged += (_, _) =>
        {
            var pointSize = (int)FontSizeSlider.Value;
            FontSizeValue.Text = $"{pointSize} pt";
            UpdateAppearancePreview();
        };
        FontFamilyBox.SelectionChanged += (_, _) => UpdateAppearancePreview();
        DarkModeCheck.Checked += (_, _) => UpdateAppearancePreview();
        DarkModeCheck.Unchecked += (_, _) => UpdateAppearancePreview();
        UpdateAppearancePreview();
        UpdateModeUi();
    }

    private void UpdateAppearancePreview()
    {
        var fontFamily = new System.Windows.Media.FontFamily(FontFamilyBox.SelectedItem as string ?? SettingsUiFontFamily);
        var pointSize = (int)FontSizeSlider.Value;
        var palette = UiThemePalette.For(DarkModeCheck.IsChecked == true);

        AppearancePreviewCard.Background = ToBrush(palette.Background);
        AppearancePreviewCard.BorderBrush = ToBrush(palette.Border);
        AppearancePreviewTab.Background = ToBrush(palette.SelectedTab);
        AppearancePreviewTabText.Foreground = ToBrush(palette.SelectedTabForeground);
        AppearancePreviewTabText.FontFamily = fontFamily;
        AppearancePreviewTabText.FontSize = UiFontSizing.QtPointSizeToWpfDip(pointSize);
        AppearancePreviewValue.Foreground = ToBrush(palette.Foreground);
        AppearancePreviewValue.FontFamily = fontFamily;
        AppearancePreviewValue.FontSize = UiFontSizing.DisplayTextDip(pointSize);
    }

    private static System.Windows.Media.SolidColorBrush ToBrush(string value) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));

    private void CaptureMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateModeUi();
    }

    private void DetectedDatabase_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DetectedDatabaseBox.SelectedItem is DatabaseCandidate candidate)
            SqliteDatabasePathBox.Text = candidate.Path;
    }

    private void UpdateModeUi()
    {
        var mode = CaptureModeBox.SelectedValue as string;
        SqliteSettingsPanel.IsEnabled = mode == "sqlite";
        JsonSettingsPanel.IsEnabled = mode == "json";
        NewMeasurementSessionButton.IsEnabled = mode == "sqlite";
    }

    private void BrowseSqlite_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "74ENのElectronicObserver.sqliteを選択",
            Filter = "ElectronicObserver SQLite (*.sqlite;*.db)|*.sqlite;*.db|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) SqliteDatabasePathBox.Text = dialog.FileName;
    }

    private void BrowseJson_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "74ENがAPI応答JSONを保存するフォルダを選択してください。",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            JsonResponseDirectoryBox.Text = dialog.SelectedPath;
    }

    private void NewMeasurementSession_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this,
                "現在の取得位置から新しい計測区間を開始します。既存の履歴やCSVは削除されません。続けますか？",
                "計測区間の開始", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        NewMeasurementSessionRequested?.Invoke();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var mode = CaptureModeBox.SelectedValue as string ?? "off";
        var previousMode = _settings.EffectiveCaptureMode;
        var continueMeasurementSeries = true;
        var startsCapture = mode is "sqlite" or "json";
        if (startsCapture && mode != previousMode && !string.IsNullOrWhiteSpace(_settings.AdmiralContinuitySeriesId))
        {
            var decision = System.Windows.MessageBox.Show(this,
                "同じ提督・同じ計測区間を続けますか？\n\nはい: 戦果時速の履歴を引き継ぐ\nいいえ: 新しい計測系列として開始\nキャンセル: 設定を適用しない",
                "計測系列の確認", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (decision == MessageBoxResult.Cancel) return;
            continueMeasurementSeries = decision == MessageBoxResult.Yes;
        }
        string? sqlitePath;
        string? jsonDirectory;
        try
        {
            sqlitePath = string.IsNullOrWhiteSpace(SqliteDatabasePathBox.Text)
                ? null : Path.GetFullPath(SqliteDatabasePathBox.Text.Trim());
            jsonDirectory = string.IsNullOrWhiteSpace(JsonResponseDirectoryBox.Text)
                ? null : Path.GetFullPath(JsonResponseDirectoryBox.Text.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            System.Windows.MessageBox.Show(this, "保存先のパスを確認してください。設定は保存しませんでした。", "設定",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (mode == "sqlite")
        {
            if (sqlitePath is null || !ElectronicObserverDatabaseDiscovery.IsLocalFile(sqlitePath))
            {
                System.Windows.MessageBox.Show(this, "ローカルにある74ENのElectronicObserver.sqliteを選択してください。ネットワーク上のDBは使用できません。", "設定",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try { new SqliteApiFileReader(sqlitePath).ValidateDatabase(); }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                System.Windows.MessageBox.Show(this, "選択したファイルは対応する74EN SQLite DBとして読み取れません。設定は保存しませんでした。",
                    "DB確認エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        if (mode == "json" && jsonDirectory is null)
        {
            System.Windows.MessageBox.Show(this, "74ENのJSON応答フォルダを指定してください。", "設定",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var updated = _settings with
        {
            CaptureMode = mode,
            LastMeasurementMode = mode is "sqlite" or "json" ? mode : _settings.EffectiveMeasurementMode,
            CaptureModeNeedsUserConfirmation = false,
            AdmiralContinuitySeriesId = continueMeasurementSeries
                ? _settings.AdmiralContinuitySeriesId
                : mode == "json" ? "json-" + Guid.NewGuid().ToString("N") : null,
            JsonExperienceSeriesId = mode is "sqlite" or "json"
                ? continueMeasurementSeries
                    ? _settings.JsonExperienceSeriesId ?? "json"
                    : "json-" + Guid.NewGuid().ToString("N")
                : _settings.JsonExperienceSeriesId,
            SqliteDatabasePath = sqlitePath,
            JsonResponseDirectory = jsonDirectory,
            DarkMode = DarkModeCheck.IsChecked == true,
            FontFamily = FontFamilyBox.SelectedItem as string ?? "Yu Gothic",
            FontSize = (int)FontSizeSlider.Value,
            AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true
        };
        SettingsApplied?.Invoke(updated, AutoStartCheck.IsChecked == true, continueMeasurementSeries);
        DialogResult = true;
    }

    private sealed record CaptureModeOption(string Label, string Value);
    private sealed record DatabaseCandidate(string Path)
    {
        public string DisplayName => Path;
    }
}
