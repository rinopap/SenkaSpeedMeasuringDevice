using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class ApiResponseTests
{
    [Fact]
    public void KeepsObservationMetadataAndBody()
    {
        var timestamp = DateTimeOffset.Parse("2026-11-26T12:00:00+09:00");
        var body = "svdata={}"u8.ToArray();
        var response = new ApiResponse("/kcsapi/api_port/port", timestamp, body, "event-1");

        Assert.Equal("/kcsapi/api_port/port", response.ApiPath);
        Assert.Equal(timestamp, response.Timestamp);
        Assert.Equal("event-1", response.EventId);
        Assert.Equal(body, response.ResponseBody.ToArray());
    }
}
