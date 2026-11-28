namespace SenkaSpeedMeasuringDevice.Core;

public static class CaptureStartPolicy
{
    public static bool ShouldStartOnApplicationLaunch(string captureMode) => captureMode is "json" or "sqlite";

    public static bool ShouldStartAfterSettingsApplied(string captureMode, bool explicitStartRequest) =>
        captureMode is "json" or "sqlite";
}
