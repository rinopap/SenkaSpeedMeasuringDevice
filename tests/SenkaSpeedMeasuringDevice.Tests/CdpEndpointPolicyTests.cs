using SenkaSpeedMeasuringDevice.Capture;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class CdpEndpointPolicyTests
{
    [Theory]
    [InlineData("ws://127.0.0.1:9222/devtools/page/abc", true)]
    [InlineData("ws://[::1]:9222/devtools/page/abc", true)]
    [InlineData("ws://localhost:9222/devtools/page/abc", true)]
    [InlineData("ws://127.0.0.1:9222/devtools/browser/abc", false)]
    [InlineData("ws://127.0.0.1:9222/devtools/page/abc/extra", false)]
    [InlineData("wss://127.0.0.1:9222/devtools/page/abc", false)]
    [InlineData("ws://192.0.2.10:9222/devtools/page/abc", false)]
    [InlineData("ws://127.0.0.1:9223/devtools/page/abc", false)]
    [InlineData("ws://user@127.0.0.1:9222/devtools/page/abc", false)]
    [InlineData("ws://127.0.0.1:9222/devtools/page/abc?token=x", false)]
    [InlineData("ws://127.0.0.1:9222/devtools/page/abc#fragment", false)]
    public void AppliesLoopbackEndpointRestrictions(string value, bool expected)
    {
        Assert.Equal(expected, CdpEndpointPolicy.IsAllowedWebSocket(new Uri(value), 9222));
    }

    [Fact]
    public void WebSocketTargetMustMatchTheListedPageTarget()
    {
        Assert.True(CdpEndpointPolicy.IsAllowedWebSocket(new Uri("ws://127.0.0.1:9222/devtools/page/abc"), 9222, "abc"));
        Assert.False(CdpEndpointPolicy.IsAllowedWebSocket(new Uri("ws://127.0.0.1:9222/devtools/page/other"), 9222, "abc"));
    }
}
