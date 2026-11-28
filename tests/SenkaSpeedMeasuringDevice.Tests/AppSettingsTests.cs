using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void ReadsAppearanceAndClampsFontSize()
    {
        var path = NewPath();
        try
        {
            File.WriteAllText(path, "{\"dark_mode\":true,\"font_family\":\"Yu Gothic UI\",\"font_size\":30,\"always_on_top\":false,\"capture_mode\":\"off\",\"last_measurement_mode\":\"json\"}");
            var loaded = AppSettings.Load(path);
            Assert.True(loaded.DarkMode);
            Assert.Equal("Yu Gothic UI", loaded.FontFamily);
            Assert.Equal(24, loaded.FontSize);
            Assert.False(loaded.AlwaysOnTop);
            Assert.Equal("off", loaded.EffectiveCaptureMode);
            Assert.Equal("json", loaded.EffectiveMeasurementMode);
        }
        finally { DeleteIfPresent(path); }
    }

    [Fact]
    public void NewConfigurationRecommendsSqliteButDoesNotInventDatabasePath()
    {
        var settings = AppSettings.Load(NewPath());
        Assert.Equal("sqlite", settings.EffectiveCaptureMode);
        Assert.Null(settings.SqliteDatabasePath);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("json")]
    [InlineData("sqlite")]
    public void SavesSupportedCaptureModesAndPaths(string mode)
    {
        var path = NewPath();
        try
        {
            var database = Path.Combine(Path.GetTempPath(), "ElectronicObserver.sqlite");
            var json = Path.Combine(Path.GetTempPath(), "74en-responses");
            new AppSettings
            {
                CaptureMode = mode,
                SqliteDatabasePath = database,
                JsonResponseDirectory = json,
                AdmiralContinuitySeriesId = "session-one:0",
                AdmiralPendingSeriesMigrationFrom = "json-session",
                JsonExperienceSeriesId = "json-ship-series",
                SqlitePortBacklogSkipToken = "handoff-token"
            }.Save(path);
            var loaded = AppSettings.Load(path);
            Assert.Equal(mode, loaded.EffectiveCaptureMode);
            Assert.Equal(Path.GetFullPath(database), loaded.SqliteDatabasePath);
            Assert.Equal(Path.GetFullPath(json), loaded.JsonResponseDirectory);
            Assert.Equal("session-one:0", loaded.AdmiralContinuitySeriesId);
            Assert.Equal("json-session", loaded.AdmiralPendingSeriesMigrationFrom);
            Assert.Equal("json-ship-series", loaded.JsonExperienceSeriesId);
            Assert.Equal("handoff-token", loaded.SqlitePortBacklogSkipToken);
            var saved = File.ReadAllText(path);
            Assert.False(saved.Contains("cdp", StringComparison.OrdinalIgnoreCase));
            Assert.False(saved.Contains("browser", StringComparison.OrdinalIgnoreCase));
        }
        finally { DeleteIfPresent(path); }
    }

    [Theory]
    [InlineData("{\"cdp_enabled\":false}", "json", false)]
    [InlineData("{\"cdp_enabled\":true}", "off", true)]
    [InlineData("{\"capture_mode\":\"cdp\"}", "off", true)]
    [InlineData("{\"capture_mode\":\"off\",\"cdp_enabled\":false}", "off", false)]
    [InlineData("{\"capture_mode\":\"json\",\"cdp_enabled\":true}", "json", false)]
    [InlineData("{\"capture_mode\":\"invalid\",\"cdp_enabled\":false}", "off", false)]
    public void MigratesLegacyModeWithoutEnablingCdp(string json, string expectedMode, bool needsConfirmation)
    {
        var path = NewPath();
        try
        {
            File.WriteAllText(path, json);
            var loaded = AppSettings.Load(path);
            Assert.Equal(expectedMode, loaded.EffectiveCaptureMode);
            Assert.Equal(needsConfirmation, loaded.CaptureModeNeedsUserConfirmation);
        }
        finally { DeleteIfPresent(path); }
    }

    [Fact]
    public void SaveIsReadableAfterReplacingExistingConfiguration()
    {
        var path = NewPath();
        try
        {
            File.WriteAllText(path, "{\"capture_mode\":\"json\"}");
            new AppSettings { CaptureMode = "sqlite", SqliteDatabasePath = Path.Combine(Path.GetTempPath(), "EO.sqlite") }.Save(path);
            Assert.Equal("sqlite", AppSettings.Load(path).EffectiveCaptureMode);
        }
        finally { DeleteIfPresent(path); }
    }

    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"senka-{Guid.NewGuid():N}.json");
    private static void DeleteIfPresent(string path) { if (File.Exists(path)) File.Delete(path); }
}
