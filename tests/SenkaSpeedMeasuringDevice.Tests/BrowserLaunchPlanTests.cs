using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class BrowserLaunchPlanTests
{
    [Fact]
    public void AuthenticationPreparationUsesDedicatedProfileWithoutCdpArguments()
    {
        var args = BrowserLaunchPlan.CreateArguments(enableCdp: false, @"C:\profiles\senka");

        Assert.DoesNotContain(args, argument => argument.StartsWith("--remote-debugging-", StringComparison.Ordinal));
        Assert.Contains(@"--user-data-dir=C:\profiles\senka", args);
        Assert.Contains(BrowserLaunchPlan.GamePage, args);
        Assert.Contains("--new-window", args);
    }

    [Fact]
    public void MeasurementModeEnablesOnlyLoopbackDynamicCdpEndpoint()
    {
        var args = BrowserLaunchPlan.CreateArguments(enableCdp: true, @"C:\profiles\senka");

        Assert.Contains("--remote-debugging-address=127.0.0.1", args);
        Assert.Contains("--remote-debugging-port=0", args);
        Assert.DoesNotContain(args, argument => argument.StartsWith("--remote-debugging-address=", StringComparison.Ordinal) &&
                                               argument != "--remote-debugging-address=127.0.0.1");
        Assert.Equal(1, args.Count(argument => argument == BrowserLaunchPlan.GamePage));
    }

    [Fact]
    public void BrowserLaunchPlanRejectsEmptyProfilePath()
    {
        Assert.Throws<ArgumentException>(() => BrowserLaunchPlan.CreateArguments(false, " "));
    }

    [Theory]
    [InlineData("cdp", false)]
    [InlineData("json", true)]
    [InlineData("off", false)]
    public void ApplicationLaunchStartsOnlyJsonCapture(string mode, bool expected)
    {
        Assert.Equal(expected, CaptureStartPolicy.ShouldStartOnApplicationLaunch(mode));
    }

    [Theory]
    [InlineData("cdp", false, false)]
    [InlineData("cdp", true, true)]
    [InlineData("json", false, true)]
    [InlineData("off", true, false)]
    public void SettingsRequireExplicitStartForCdp(string mode, bool explicitStart, bool expected)
    {
        Assert.Equal(expected, CaptureStartPolicy.ShouldStartAfterSettingsApplied(mode, explicitStart));
    }
}
