using System.Text.Json;

namespace SenkaSpeedMeasuringDevice.Core;

/// <summary>Parses only the four EO 5.4.3EN allowlisted successful result APIs.</summary>
public static class ShipExpGainParser
{
    private const int MaximumBodyBytes = 8 * 1024 * 1024;
    private const int MaximumShipsPerFleet = 7;
    private static readonly IReadOnlyDictionary<string, ShipExpGainKind> ApiKinds = new Dictionary<string, ShipExpGainKind>(StringComparer.Ordinal)
    {
        ["/kcsapi/api_req_sortie/battleresult"] = ShipExpGainKind.Sortie,
        ["/kcsapi/api_req_combined_battle/battleresult"] = ShipExpGainKind.CombinedBattle,
        ["/kcsapi/api_req_practice/battle_result"] = ShipExpGainKind.Practice,
        ["/kcsapi/api_req_mission/result"] = ShipExpGainKind.Expedition
    };

    public static ShipExpParseResult Parse(ApiResponse response)
    {
        if (!TryGetKind(response.ApiPath, out var kind)) return ShipExpParseResult.Invalid("unsupported-api");
        if (response.SourceRecordId is not > 0 || string.IsNullOrWhiteSpace(response.SourceGenerationId) ||
            string.IsNullOrWhiteSpace(response.MeasurementSeriesId))
            return ShipExpParseResult.Invalid("missing-source-identity");

        try
        {
            var body = response.ResponseBody.Span;
            if (body.Length is 0 or > MaximumBodyBytes) return ShipExpParseResult.Invalid("body-size");
            if (body.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) body = body[3..];
            var text = System.Text.Encoding.UTF8.GetString(body).Trim();
            if (text.StartsWith("svdata=", StringComparison.Ordinal)) text = text[7..].Trim();
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("api_result", out var result) || result.ValueKind != JsonValueKind.Number ||
                !result.TryGetInt32(out var resultCode) || resultCode != 1)
                return ShipExpParseResult.Invalid("api-result-failed");
            if (!root.TryGetProperty("api_data", out var data) || data.ValueKind != JsonValueKind.Object)
                return ShipExpParseResult.Invalid("missing-api-data");

            long total;
            switch (kind)
            {
                case ShipExpGainKind.Sortie:
                case ShipExpGainKind.Practice:
                    if (!TrySumBattleArray(data, "api_get_ship_exp", out total, out var issue))
                        return ShipExpParseResult.Invalid(issue);
                    break;
                case ShipExpGainKind.CombinedBattle:
                    if (!TrySumBattleArray(data, "api_get_ship_exp", out var main, out issue) ||
                        !TrySumBattleArray(data, "api_get_ship_exp_combined", out var escort, out issue))
                        return ShipExpParseResult.Invalid(issue);
                    try { total = checked(main + escort); }
                    catch (OverflowException) { return ShipExpParseResult.Invalid("experience-overflow"); }
                    break;
                case ShipExpGainKind.Expedition:
                    if (!TrySumExpeditionArray(data, out total, out issue))
                        return ShipExpParseResult.Invalid(issue);
                    break;
                default:
                    return ShipExpParseResult.Invalid("unsupported-api");
            }

            return new ShipExpParseResult(new ShipExpGainEvent(
                response.EventId,
                response.SourceGenerationId!,
                response.MeasurementSeriesId!,
                response.SourceRecordId.Value,
                response.Timestamp.ToUniversalTime(),
                kind,
                total), null);
        }
        catch (JsonException) { return ShipExpParseResult.Invalid("invalid-json"); }
        catch (ArgumentException) { return ShipExpParseResult.Invalid("invalid-body-encoding"); }
    }

    public static bool IsSupportedStream(string apiName) =>
        ApiKinds.ContainsKey(apiName.StartsWith("/kcsapi/", StringComparison.Ordinal) ? apiName : "/kcsapi/" + apiName);

    private static bool TrySumBattleArray(JsonElement data, string property, out long total, out string issue)
    {
        total = 0;
        issue = "missing-ship-exp-array";
        if (!data.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return false;
        // EO 5.4.3EN BattleResultData.ExpList discards element 0 and maps the
        // remaining up-to-seven values to fleet slots. Accept only those known shapes.
        if (array.GetArrayLength() is < 7 or > MaximumShipsPerFleet + 1)
        {
            issue = "unsupported-ship-exp-array-length";
            return false;
        }

        var index = 0;
        try
        {
            foreach (var value in array.EnumerateArray())
            {
                if (index++ == 0) continue; // 74EN's result model explicitly drops the dummy element.
                if (!value.TryGetInt64(out var experience))
                {
                    issue = "invalid-ship-exp-value";
                    return false;
                }
                if (experience == -1) continue; // 74EN documents -1 as an unoccupied/missing slot.
                if (experience < 0)
                {
                    issue = "negative-ship-exp-value";
                    return false;
                }
                total = checked(total + experience);
            }
        }
        catch (OverflowException)
        {
            issue = "experience-overflow";
            return false;
        }
        issue = string.Empty;
        return true;
    }

    private static bool TrySumExpeditionArray(JsonElement data, out long total, out string issue)
    {
        total = 0;
        issue = "missing-ship-exp-array";
        if (!data.TryGetProperty("api_get_ship_exp", out var array) || array.ValueKind != JsonValueKind.Array) return false;
        if (array.GetArrayLength() is < 1 or > 6)
        {
            issue = "unsupported-expedition-array-length";
            return false;
        }
        try
        {
            foreach (var value in array.EnumerateArray())
            {
                if (!value.TryGetInt64(out var experience))
                {
                    issue = "invalid-ship-exp-value";
                    return false;
                }
                if (experience < 0)
                {
                    issue = "negative-ship-exp-value";
                    return false;
                }
                total = checked(total + experience);
            }
        }
        catch (OverflowException)
        {
            issue = "experience-overflow";
            return false;
        }
        issue = string.Empty;
        return true;
    }

    private static bool TryGetKind(string apiPath, out ShipExpGainKind kind)
    {
        return ApiKinds.TryGetValue(apiPath, out kind);
    }
}
