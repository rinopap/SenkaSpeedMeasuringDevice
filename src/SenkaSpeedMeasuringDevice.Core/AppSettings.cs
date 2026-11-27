using System.Text.Json;
using System.Text.Json.Serialization;

namespace SenkaSpeedMeasuringDevice.Core;

public sealed record AppSettings
{
    [JsonPropertyName("dark_mode")] public bool DarkMode { get; init; }
    [JsonPropertyName("font_family")] public string FontFamily { get; init; } = "Yu Gothic";
    [JsonPropertyName("font_size")] public int FontSize { get; init; } = 12;
    [JsonPropertyName("always_on_top")] public bool AlwaysOnTop { get; init; } = true;
    [JsonPropertyName("cdp_enabled")] public bool CdpEnabled { get; init; }
    [JsonPropertyName("cdp_debug_port")] public int CdpDebugPort { get; init; } = 9222;
    [JsonPropertyName("server_id")] public int? ServerId { get; init; }
    [JsonPropertyName("capture_mode")] public string CaptureMode { get; init; } = "off";
    [JsonPropertyName("browser_id")] public string? BrowserId { get; init; }

    public string EffectiveCaptureMode => CaptureMode is "off" or "cdp" or "json"
        ? CaptureMode
        : CdpEnabled ? "cdp" : "json";

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new AppSettings();
        try
        {
            var bytes = File.ReadAllBytes(path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(bytes);
            if (loaded is null) return new AppSettings();
            using var document = JsonDocument.Parse(bytes);
            var hasCaptureMode = document.RootElement.ValueKind == JsonValueKind.Object &&
                                 document.RootElement.TryGetProperty("capture_mode", out _);
            return loaded with
            {
                FontFamily = string.IsNullOrWhiteSpace(loaded.FontFamily) ? "Yu Gothic" : loaded.FontFamily,
                FontSize = Math.Clamp(loaded.FontSize, 8, 24),
                CdpDebugPort = loaded.CdpDebugPort is >= 1 and <= 65535 ? loaded.CdpDebugPort : 9222,
                ServerId = loaded.ServerId is { } serverId && GameServerRegistry.TryGet(serverId, out _) ? serverId : null,
                CaptureMode = hasCaptureMode && loaded.CaptureMode is "off" or "cdp" or "json"
                    ? loaded.CaptureMode
                    : loaded.CdpEnabled ? "cdp" : "json",
                BrowserId = loaded.BrowserId is "chrome" or "edge" or "74en" ? loaded.BrowserId : null
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
