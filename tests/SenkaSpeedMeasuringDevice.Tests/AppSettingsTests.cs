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
            File.WriteAllText(path, "{\"dark_mode\":true,\"font_family\":\"Yu Gothic UI\",\"font_size\":30,\"always_on_top\":false}");
            var loaded = AppSettings.Load(path);
            Assert.True(loaded.DarkMode);
            Assert.Equal("Yu Gothic UI", loaded.FontFamily);
            Assert.Equal(24, loaded.FontSize);
            Assert.False(loaded.AlwaysOnTop);
            loaded.Save(path);
            Assert.Contains("\"dark_mode\": true", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

