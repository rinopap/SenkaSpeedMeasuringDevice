using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void ReadsAndWritesCppCompatibleSnakeCaseSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"senka-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"dark_mode\":true,\"font_family\":\"Yu Gothic UI\",\"font_size\":30,\"always_on_top\":false,\"cdp_enabled\":true,\"cdp_debug_port\":9224,\"cdp_page_url\":\"https://arbitrary.example/\",\"cdp_api_hosts\":\"arbitrary.example\"}");
            var loaded = AppSettings.Load(path);
            Assert.True(loaded.DarkMode);
            Assert.Equal("Yu Gothic UI", loaded.FontFamily);
            Assert.Equal(24, loaded.FontSize);
            Assert.False(loaded.AlwaysOnTop);
            Assert.True(loaded.CdpEnabled);
            Assert.Equal(9224, loaded.CdpDebugPort);
            Assert.Null(loaded.ServerId);
            loaded.Save(path);
            var saved = File.ReadAllText(path);
            Assert.Contains("\"dark_mode\": true", saved);
            Assert.DoesNotContain("cdp_page_url", saved);
            Assert.DoesNotContain("cdp_api_hosts", saved);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(0, null)]
    [InlineData(21, null)]
    public void LoadsOnlyKnownServerIds(int? inputId, int? expectedId)
    {
        var path = Path.Combine(Path.GetTempPath(), $"senka-{Guid.NewGuid():N}.json");
        try
        {
            var value = inputId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
            File.WriteAllText(path, $"{{\"server_id\":{value}}}");
            Assert.Equal(expectedId, AppSettings.Load(path).ServerId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
