using System.Text;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class ApiResponseParserTests
{
    [Theory]
    [InlineData("{\"api_result\":1,\"api_data\":{\"api_basic\":{\"api_experience\":1234567890123},\"api_ship\":[{\"api_exp\":[100,0]},{\"api_exp\":[250,0]}]}}")]
    [InlineData("svdata={\"api_result\":1,\"api_data\":{\"api_basic\":{\"api_experience\":1234567890123},\"api_ship\":[{\"api_exp\":[100,0]},{\"api_exp\":[250,0]}]}}")]
    public void ParsesAdmiralAndAllShipExperience(string body)
    {
        var parsed = ApiResponseParser.Parse(Response(body));
        Assert.NotNull(parsed);
        Assert.Equal(1_234_567_890_123, parsed.AdmiralExperience);
        Assert.Equal(350, parsed.ShipTotalExperience);
    }

    [Fact]
    public void ParsesPercentEncodedJsonWithoutTreatingPlusAsSpace()
    {
        var json = "{\"api_result\":1,\"api_data\":{\"api_basic\":{\"api_experience\":42}}}";
        var encoded = Uri.EscapeDataString(json).Replace("%7B", "%7b", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(42, ApiResponseParser.Parse(Response(encoded))?.AdmiralExperience);
    }

    [Fact]
    public void RejectsInvalidJsonAndPartialShipRows()
    {
        Assert.Null(ApiResponseParser.Parse(Response("not json")));
        var body = "{\"api_result\":1,\"api_data\":{\"api_ship\":[{\"api_exp\":[]}]}}";
        Assert.Null(ApiResponseParser.Parse(Response(body)));
    }

    [Theory]
    [InlineData("{\"api_result\":0,\"api_data\":{\"api_basic\":{\"api_experience\":42}}}")]
    [InlineData("{\"api_data\":{\"api_basic\":{\"api_experience\":42}}}")]
    [InlineData("[ {\"api_result\":1,\"api_data\":{} } ]")]
    [InlineData("prefix {\"api_result\":1,\"api_data\":{\"api_basic\":{\"api_experience\":42}}}")]
    [InlineData("{\"api_result\":1,\"api_data\":null}")]
    [InlineData("{\"api_result\":1,\"api_data\":{\"api_basic\":{\"api_experience\":-1}}}")]
    [InlineData("{\"api_result\":1,\"api_data\":{\"api_ship\":[null]}}")]
    [InlineData("{\"api_result\":1,\"api_data\":{\"api_ship\":[\"ship\"]}}")]
    public void RejectsUnsuccessfulOrMalformedPayloads(string body) => Assert.Null(ApiResponseParser.Parse(Response(body)));

    [Fact]
    public void InvalidShipTotalDoesNotDiscardValidAdmiralExperience()
    {
        var body = "{\"api_result\":1,\"api_data\":{\"api_basic\":{\"api_experience\":42},\"api_ship\":[{\"api_exp\":[9223372036854775807]},{\"api_exp\":[1]}]}}";
        var parsed = ApiResponseParser.Parse(Response(body));
        Assert.Equal(42, parsed?.AdmiralExperience);
        Assert.Null(parsed?.ShipTotalExperience);
    }

    [Fact]
    public void RejectsNonTargetApiPath() =>
        Assert.Null(ApiResponseParser.Parse(new ApiResponse("/kcsapi/api_get_member/ship2", DateTimeOffset.UtcNow,
            Encoding.UTF8.GetBytes("{\"api_result\":1,\"api_data\":{}}"), "test")));

    private static ApiResponse Response(string body) => new("/kcsapi/api_port/port",
        DateTimeOffset.Parse("2026-11-26T08:30:00+09:00"), Encoding.UTF8.GetBytes(body), "test");
}
