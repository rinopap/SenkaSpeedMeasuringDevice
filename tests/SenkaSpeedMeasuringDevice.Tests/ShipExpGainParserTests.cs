using System.Text;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class ShipExpGainParserTests
{
    [Theory]
    [InlineData("/kcsapi/api_req_sortie/battleresult", "\"api_get_ship_exp\":[9000,100,200,300,400,500,600]", ShipExpGainKind.Sortie, 2100)]
    [InlineData("/kcsapi/api_req_practice/battle_result", "\"api_get_ship_exp\":[9000,1,2,3,4,5,6]", ShipExpGainKind.Practice, 21)]
    public void NormalAndPracticeResultsDropOnlyTheKnownDummySlot(string path, string data, ShipExpGainKind kind, long expected)
    {
        var parsed = ShipExpGainParser.Parse(Response(path, $"{{\"api_result\":1,\"api_data\":{{{data}}}}}"));
        Assert.Null(parsed.IssueCode);
        Assert.NotNull(parsed.Event);
        Assert.Equal(kind, parsed.Event.Kind);
        Assert.Equal(expected, parsed.Event.GainedExp);
        Assert.Equal("ApiReportedShipExp", parsed.Event.Meaning);
    }

    [Fact]
    public void CombinedBattleSumsMainAndEscortAndSkipsBothDummySlots()
    {
        var body = "{\"api_result\":1,\"api_data\":{" +
                   "\"api_get_ship_exp\":[99999,100,200,300,400,500,600]," +
                   "\"api_get_ship_exp_combined\":[88888,10,20,30,40,50,60]}}";
        var parsed = ShipExpGainParser.Parse(Response("/kcsapi/api_req_combined_battle/battleresult", body));
        Assert.Equal(2310, Assert.IsType<ShipExpGainEvent>(parsed.Event).GainedExp);
        Assert.Equal(ShipExpGainKind.CombinedBattle, parsed.Event.Kind);
    }

    [Fact]
    public void SevenMemberFleetShapeIsAcceptedAndDashIsExcluded()
    {
        var body = "{\"api_result\":1,\"api_data\":{\"api_get_ship_exp\":[777,1,2,3,4,5,6,7]}}";
        var parsed = ShipExpGainParser.Parse(Response("/kcsapi/api_req_sortie/battleresult", body));
        Assert.Equal(28, Assert.IsType<ShipExpGainEvent>(parsed.Event).GainedExp);

        var withMissingSlot = "{\"api_result\":1,\"api_data\":{\"api_get_ship_exp\":[777,1,-1,3,4,5,6]}}";
        var missing = ShipExpGainParser.Parse(Response("/kcsapi/api_req_sortie/battleresult", withMissingSlot));
        Assert.Equal(19, Assert.IsType<ShipExpGainEvent>(missing.Event).GainedExp);
    }

    [Fact]
    public void ExpeditionArrayIsDirectlySlotAlignedAndZeroGainIsValid()
    {
        var body = "{\"api_result\":1,\"api_data\":{\"api_get_ship_exp\":[10,20,30,40,50,60]}}";
        var parsed = ShipExpGainParser.Parse(Response("/kcsapi/api_req_mission/result", body));
        Assert.Equal(210, Assert.IsType<ShipExpGainEvent>(parsed.Event).GainedExp);

        var zero = ShipExpGainParser.Parse(Response("/kcsapi/api_req_mission/result",
            "{\"api_result\":1,\"api_data\":{\"api_get_ship_exp\":[0,0,0,0,0,0]}}"));
        Assert.Equal(0, Assert.IsType<ShipExpGainEvent>(zero.Event).GainedExp);
    }

    [Theory]
    [InlineData("{\"api_result\":0,\"api_data\":{\"api_get_ship_exp\":[0,1,2,3,4,5,6]}}", "api-result-failed")]
    [InlineData("{\"api_result\":1,\"api_data\":{}}", "missing-ship-exp-array")]
    [InlineData("{\"api_result\":1,\"api_data\":{\"api_get_ship_exp\":[0,1,2]}}", "unsupported-ship-exp-array-length")]
    [InlineData("{\"api_result\":1,\"api_data\":{\"api_get_ship_exp\":[0,1,-2,3,4,5,6]}}", "negative-ship-exp-value")]
    [InlineData("{\"api_result\":1,\"api_data\":{\"api_get_ship_exp\":[0,9223372036854775807,1,0,0,0,0]}}", "experience-overflow")]
    public void InvalidResponsesDoNotBecomeZeroGainEvents(string body, string issue)
    {
        var parsed = ShipExpGainParser.Parse(Response("/kcsapi/api_req_sortie/battleresult", body));
        Assert.Null(parsed.Event);
        Assert.Equal(issue, parsed.IssueCode);
    }

    [Fact]
    public void MissingCombinedEscortArrayIsNotAssumedToBeZero()
    {
        var parsed = ShipExpGainParser.Parse(Response("/kcsapi/api_req_combined_battle/battleresult",
            "{\"api_result\":1,\"api_data\":{\"api_get_ship_exp\":[0,1,2,3,4,5,6]}}"));
        Assert.Null(parsed.Event);
        Assert.Equal("missing-ship-exp-array", parsed.IssueCode);
    }

    [Fact]
    public void UnsupportedApiAndMissingStableSourceIdentityAreRejected()
    {
        Assert.Equal("unsupported-api", ShipExpGainParser.Parse(Response("/kcsapi/api_req_sortie/battle", "{}")).IssueCode);
        Assert.Equal("missing-source-identity", ShipExpGainParser.Parse(new ApiResponse(
            "/kcsapi/api_req_mission/result", DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes("{}"), "id")).IssueCode);
    }

    private static ApiResponse Response(string path, string body) => new(path,
        DateTimeOffset.Parse("2026-11-28T12:34:00+00:00"), Encoding.UTF8.GetBytes(body),
        "gen:" + path + ":42", 42, "generation-a", "series-a");
}
