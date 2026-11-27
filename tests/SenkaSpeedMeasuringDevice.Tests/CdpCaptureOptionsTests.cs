using SenkaSpeedMeasuringDevice.Capture;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class CdpCaptureOptionsTests
{
    [Fact]
    public void AcceptsKnownServerAndLoopbackPort()
    {
        new CdpCaptureOptions(9222, 20).Validate();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(65536, 1)]
    [InlineData(9222, 0)]
    [InlineData(9222, 21)]
    public void RejectsInvalidPortOrServer(int port, int serverId)
    {
        Assert.ThrowsAny<ArgumentException>(() => new CdpCaptureOptions(port, serverId).Validate());
    }
}
