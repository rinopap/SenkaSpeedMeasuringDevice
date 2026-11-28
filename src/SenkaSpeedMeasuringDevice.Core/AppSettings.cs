using System.Text.Json;
using System.Text.Json.Serialization;

namespace SenkaSpeedMeasuringDevice.Core;

public sealed record AppSettings
{
    [JsonPropertyName("dark_mode")] public bool DarkMode { get; init; }
    [JsonPropertyName("font_family")] public string FontFamily { get; init; } = "Yu Gothic";
    [JsonPropertyName("font_size")] public int FontSize { get; init; } = 12;
    [JsonPropertyName("always_on_top")] public bool AlwaysOnTop { get; init; } = true;
    [JsonPropertyName("capture_mode")] public string CaptureMode { get; init; } = "sqlite";
    [JsonPropertyName("last_measurement_mode")] public string LastMeasurementMode { get; init; } = "sqlite";
    [JsonPropertyName("sqlite_database_path")] public string? SqliteDatabasePath { get; init; }
    [JsonPropertyName("json_response_directory")] public string? JsonResponseDirectory { get; init; }
    [JsonPropertyName("admiral_continuity_series_id")] public string? AdmiralContinuitySeriesId { get; init; }
    [JsonPropertyName("admiral_pending_series_migration_from")] public string? AdmiralPendingSeriesMigrationFrom { get; init; }
    [JsonPropertyName("sqlite_port_backlog_skip_token")] public string? SqlitePortBacklogSkipToken { get; init; }
    [JsonPropertyName("json_experience_series_id")] public string? JsonExperienceSeriesId { get; init; }
    [JsonIgnore] public bool CaptureModeNeedsUserConfirmation { get; init; }

    public string EffectiveCaptureMode => CaptureMode is "off" or "json" or "sqlite" ? CaptureMode : "off";
    public string EffectiveMeasurementMode => EffectiveCaptureMode is "json" or "sqlite"
        ? EffectiveCaptureMode
        : LastMeasurementMode is "json" or "sqlite" ? LastMeasurementMode : "sqlite";

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
            var oldCdpEnabled = document.RootElement.ValueKind == JsonValueKind.Object &&
                                document.RootElement.TryGetProperty("cdp_enabled", out var cdpValue) &&
                                cdpValue.ValueKind == JsonValueKind.True;
            var oldCdpMode = string.Equals(loaded.CaptureMode, "cdp", StringComparison.OrdinalIgnoreCase);
            var migratedMode = !hasCaptureMode
                ? oldCdpEnabled ? "off" : "json"
                : loaded.CaptureMode switch
                {
                    "off" or "json" or "sqlite" => loaded.CaptureMode,
                    "cdp" => "off",
                    _ => "off"
                };
            return loaded with
            {
                FontFamily = string.IsNullOrWhiteSpace(loaded.FontFamily) ? "Yu Gothic" : loaded.FontFamily,
                FontSize = Math.Clamp(loaded.FontSize, 8, 24),
                CaptureMode = migratedMode,
                LastMeasurementMode = loaded.LastMeasurementMode is "json" or "sqlite"
                    ? loaded.LastMeasurementMode
                    : migratedMode is "json" or "sqlite" ? migratedMode : "sqlite",
                CaptureModeNeedsUserConfirmation = oldCdpMode || (!hasCaptureMode && oldCdpEnabled),
                SqliteDatabasePath = string.IsNullOrWhiteSpace(loaded.SqliteDatabasePath)
                    ? null : Path.GetFullPath(loaded.SqliteDatabasePath),
                JsonResponseDirectory = string.IsNullOrWhiteSpace(loaded.JsonResponseDirectory)
                    ? null : Path.GetFullPath(loaded.JsonResponseDirectory),
                AdmiralContinuitySeriesId = string.IsNullOrWhiteSpace(loaded.AdmiralContinuitySeriesId)
                    ? null : loaded.AdmiralContinuitySeriesId,
                AdmiralPendingSeriesMigrationFrom = string.IsNullOrWhiteSpace(loaded.AdmiralPendingSeriesMigrationFrom)
                    ? null : loaded.AdmiralPendingSeriesMigrationFrom,
                JsonExperienceSeriesId = string.IsNullOrWhiteSpace(loaded.JsonExperienceSeriesId)
                    ? null : loaded.JsonExperienceSeriesId
            };
        }
        catch (JsonException) { return new AppSettings(); }
        catch (IOException) { return new AppSettings(); }
        catch (ArgumentException) { return new AppSettings(); }
        catch (NotSupportedException) { return new AppSettings(); }
    }

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch (IOException) { }
        }
    }
}
