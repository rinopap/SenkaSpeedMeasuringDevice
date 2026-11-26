using SenkaSpeedMeasuringDevice.Capture;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class CdpCaptureOptionsTests
{
    [Fact]
    public void AcceptsAnExplicitPageAndApiHost()
    {
        var options = new CdpCaptureOptions(9222, "https://game.example.test/start",
            new HashSet<string>(["api.example.test"]));
        options.Validate();
    }

    [Theory]
    [InlineData(0, "http://game.example.test/", "api.example.test")]
    [InlineData(9222, "file:///tmp/game.html", "api.example.test")]
    [InlineData(9222, "https://user:secret@game.example.test/", "api.example.test")]
    public void RejectsInvalidTargetConfiguration(int port, string pageUrl, string apiHost)
    {
        var options = new CdpCaptureOptions(port, pageUrl, new HashSet<string>([apiHost]));
        Assert.ThrowsAny<ArgumentException>(options.Validate);
    }

    [Fact]
    public void RejectsMissingApiHostAllowlist()
    {
        var options = new CdpCaptureOptions(9222, "https://game.example.test/", new HashSet<string>());
        Assert.ThrowsAny<ArgumentException>(options.Validate);
    }
}

