using System.Text.Json;
using System.Text.Json.Serialization;

namespace SenkaSpeedMeasuringDevice.Core;

public sealed record AppSettings
{
    [JsonPropertyName("dark_mode")] public bool DarkMode { get; init; }
    [JsonPropertyName("font_family")] public string FontFamily { get; init; } = "Yu Gothic";
    [JsonPropertyName("font_size")] public int FontSize { get; init; } = 12;
    [JsonPropertyName("always_on_top")] public bool AlwaysOnTop { get; init; } = true;

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(path));
            return loaded is null ? new AppSettings() : loaded with
            {
                FontFamily = string.IsNullOrWhiteSpace(loaded.FontFamily) ? "Yu Gothic" : loaded.FontFamily,
                FontSize = Math.Clamp(loaded.FontSize, 8, 24)
            };
        }
        catch (JsonException) { return new AppSettings(); }
        catch (IOException) { return new AppSettings(); }
    }

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

