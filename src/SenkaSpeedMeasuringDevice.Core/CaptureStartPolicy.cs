namespace SenkaSpeedMeasuringDevice.Core;

public static class CaptureStartPolicy
{
    public static bool ShouldStartOnApplicationLaunch(string captureMode) => captureMode == "json";

    public static bool ShouldStartAfterSettingsApplied(string captureMode, bool explicitStartRequest) =>
        captureMode == "json" || captureMode == "cdp" && explicitStartRequest;
}
