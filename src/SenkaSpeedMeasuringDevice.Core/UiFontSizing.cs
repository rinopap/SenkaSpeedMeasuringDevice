namespace SenkaSpeedMeasuringDevice.Core;

/// <summary>Maps the persisted Qt point-size preference to WPF device-independent units.</summary>
public static class UiFontSizing
{
    public const string MaximumExpRateWidthSample = "・00:00~01:00 Exp時速 1,234,567";

    public static double QtPointSizeToWpfDip(double pointSize)
    {
        if (!double.IsFinite(pointSize) || pointSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pointSize));
        return pointSize * 96.0 / 72.0;
    }

    public static double DisplayTextDip(int configuredPointSize) =>
        QtPointSizeToWpfDip(configuredPointSize + 2.0);
}
