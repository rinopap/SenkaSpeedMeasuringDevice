using System.Globalization;
using System.Text.Json;

namespace SenkaSpeedMeasuringDevice.Core;

public static class ApiResponseParser
{
    private const int MaximumResponseBytes = 8 * 1024 * 1024;

    public static ExperienceSnapshot? Parse(ApiResponse response)
    {
        if (!string.Equals(response.ApiPath, "/kcsapi/api_port/port", StringComparison.Ordinal)) return null;
        try
        {
            var body = response.ResponseBody.Span;
            if (body.Length == 0 || body.Length > MaximumResponseBytes) return null;
            if (body.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) body = body[3..];
            var text = System.Text.Encoding.UTF8.GetString(body).Trim();
            if (text.StartsWith("svdata=", StringComparison.Ordinal)) text = text[7..].Trim();
            else if (text.StartsWith("%7b", StringComparison.OrdinalIgnoreCase))
            {
                text = Uri.UnescapeDataString(text);
                if (!text.StartsWith('{')) return null;
            }

            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("api_result", out var result) ||
                result.ValueKind != JsonValueKind.Number || !result.TryGetInt32(out var resultCode) || resultCode != 1)
                return null;

            var apiData = GetObject(document.RootElement, "api_data");
            if (apiData.ValueKind != JsonValueKind.Object) return null;

            var apiBasic = GetObject(apiData, "api_basic");
            var admiral = GetNonNegativeInt64(apiBasic, "api_experience");

            long? shipTotal = null;
            if (apiData.TryGetProperty("api_ship", out var ships) && ships.ValueKind == JsonValueKind.Array &&
                ships.GetArrayLength() > 0)
            {
                long total = 0;
                var valid = true;
                foreach (var ship in ships.EnumerateArray())
                {
                    if (ship.ValueKind != JsonValueKind.Object || !ship.TryGetProperty("api_exp", out var exp) ||
                        exp.ValueKind != JsonValueKind.Array || exp.GetArrayLength() == 0 ||
                        !TryGetInt64(exp[0], out var value) || value < 0)
                    {
                        valid = false;
                        break;
                    }
                    try { total = checked(total + value); }
                    catch (OverflowException) { valid = false; break; }
                }
                if (valid) shipTotal = total;
            }

            if (admiral is null && shipTotal is null) return null;
            return new ExperienceSnapshot(response.Timestamp, admiral, shipTotal, response.EventId);
        }
        catch (Exception ex) when (ex is JsonException or UriFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses only the admiral experience from a 74EN SQLite port record. The SQLite
    /// representation may intentionally omit api_ship, so it can never create a ship
    /// total observation.
    /// </summary>
    public static ExperienceSnapshot? ParseAdmiralPort(ApiResponse response)
    {
        if (!string.Equals(response.ApiPath, "/kcsapi/api_port/port", StringComparison.Ordinal)) return null;
        try
        {
            var body = response.ResponseBody.Span;
            if (body.Length == 0 || body.Length > MaximumResponseBytes) return null;
            if (body.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) body = body[3..];
            var text = System.Text.Encoding.UTF8.GetString(body).Trim();
            if (text.StartsWith("svdata=", StringComparison.Ordinal)) text = text[7..].Trim();
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("api_result", out var result) ||
                result.ValueKind != JsonValueKind.Number || !result.TryGetInt32(out var resultCode) || resultCode != 1)
                return null;
            var apiData = GetObject(root, "api_data");
            var apiBasic = GetObject(apiData, "api_basic");
            var admiral = GetNonNegativeInt64(apiBasic, "api_experience");
            return admiral is null ? null : new ExperienceSnapshot(response.Timestamp, admiral, null, response.EventId);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static JsonElement GetObject(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object ? value : default;

    private static long? GetNonNegativeInt64(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        TryGetInt64(value, out var parsed) && parsed >= 0 ? parsed : null;

    private static bool TryGetInt64(JsonElement element, out long value)
    {
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetInt64(out value);
        if (element.ValueKind == JsonValueKind.String)
            return long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        value = 0;
        return false;
    }
}
