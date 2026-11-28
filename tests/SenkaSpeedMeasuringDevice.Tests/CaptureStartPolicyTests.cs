using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class CaptureStartPolicyTests
{
    [Theory]
    [InlineData("sqlite", true)]
    [InlineData("json", true)]
    [InlineData("off", false)]
    [InlineData("cdp", false)]
    [InlineData("unknown", false)]
    public void StartsOnlySupportedCaptureModesAtApplicationLaunch(string mode, bool expected) =>
        Assert.Equal(expected, CaptureStartPolicy.ShouldStartOnApplicationLaunch(mode));

    [Theory]
    [InlineData("sqlite", false, true)]
    [InlineData("json", false, true)]
    [InlineData("off", true, false)]
    [InlineData("cdp", true, false)]
    public void AppliesOnlySupportedCaptureModes(string mode, bool explicitStart, bool expected) =>
        Assert.Equal(expected, CaptureStartPolicy.ShouldStartAfterSettingsApplied(mode, explicitStart));
}
