using System.Text;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class ApiResponseParserTests
{
    [Theory]
    [InlineData("{\"api_data\":{\"api_basic\":{\"api_experience\":1234567890123},\"api_ship\":[{\"api_exp\":[100,0]},{\"api_exp\":[250,0]}]}}")]
    [InlineData("svdata={\"api_data\":{\"api_basic\":{\"api_experience\":1234567890123},\"api_ship\":[{\"api_exp\":[100,0]},{\"api_exp\":[250,0]}]}}")]
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
        var json = "{\"api_data\":{\"api_basic\":{\"api_experience\":42}}}";
        var encoded = Uri.EscapeDataString(json).Replace("%7B", "%7b", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(42, ApiResponseParser.Parse(Response(encoded))?.AdmiralExperience);
    }

    [Fact]
    public void RejectsInvalidJsonAndPartialShipRows()
    {
        Assert.Null(ApiResponseParser.Parse(Response("not json")));
        var body = "{\"api_data\":{\"api_ship\":[{\"api_exp\":[]}]}}";
        Assert.Null(ApiResponseParser.Parse(Response(body)));
    }

    private static ApiResponse Response(string body) => new("/kcsapi/api_port/port",
        DateTimeOffset.Parse("2026-11-26T08:30:00+09:00"), Encoding.UTF8.GetBytes(body), "test");
}

