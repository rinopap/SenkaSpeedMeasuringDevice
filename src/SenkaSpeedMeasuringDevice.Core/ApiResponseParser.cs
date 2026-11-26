using System.Globalization;
using System.Text.Json;

namespace SenkaSpeedMeasuringDevice.Core;

public static class ApiResponseParser
{
    public static ExperienceSnapshot? Parse(ApiResponse response)
    {
        var body = response.ResponseBody.Span;
        if (body.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) body = body[3..];
        var text = System.Text.Encoding.UTF8.GetString(body).Trim();
        if (text.StartsWith("svdata=", StringComparison.Ordinal)) text = text[7..].Trim();

        if (text.StartsWith("%7b", StringComparison.OrdinalIgnoreCase))
        {
            try { text = Uri.UnescapeDataString(text); }
            catch (UriFormatException) { return null; }
        }

        var objectStart = text.IndexOf('{');
        if (objectStart < 0) return null;
        text = text[objectStart..];

        try
        {
            using var document = JsonDocument.Parse(text);
            var apiData = GetObject(document.RootElement, "api_data");
            var apiBasic = GetObject(apiData, "api_basic");
            var admiral = GetInt64(apiBasic, "api_experience");

            long? shipTotal = null;
            if (apiData.TryGetProperty("api_ship", out var ships) &&
                ships.ValueKind == JsonValueKind.Array && ships.GetArrayLength() > 0)
            {
                long total = 0;
                var valid = true;
                foreach (var ship in ships.EnumerateArray())
                {
                    if (!ship.TryGetProperty("api_exp", out var exp) ||
                        exp.ValueKind != JsonValueKind.Array || exp.GetArrayLength() == 0 ||
                        !TryGetInt64(exp[0], out var value))
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
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement GetObject(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object ? value : default;

    private static long? GetInt64(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        TryGetInt64(value, out var parsed) ? parsed : null;

    private static bool TryGetInt64(JsonElement element, out long value)
    {
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetInt64(out value);
        if (element.ValueKind == JsonValueKind.String)
            return long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        value = 0;
        return false;
    }
}

