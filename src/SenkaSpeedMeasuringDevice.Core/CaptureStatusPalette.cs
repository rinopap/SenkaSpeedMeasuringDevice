namespace SenkaSpeedMeasuringDevice.Core;

public enum CaptureStatusTone
{
    Normal,
    Info,
    Warning,
    Error
}

/// <summary>Theme-aware colors for the compact capture status line.</summary>
public static class CaptureStatusPalette
{
    public static string ForegroundHex(bool darkMode, CaptureStatusTone tone) => (darkMode, tone) switch
    {
        (true, CaptureStatusTone.Normal) => "#E8EAED",
        (true, CaptureStatusTone.Info) => "#DADCE0",
        (true, CaptureStatusTone.Warning) => "#FDD663",
        (true, CaptureStatusTone.Error) => "#F28B82",
        (false, CaptureStatusTone.Normal) => "#202124",
        (false, CaptureStatusTone.Info) => "#3C4043",
        (false, CaptureStatusTone.Warning) => "#8A5A00",
        (false, CaptureStatusTone.Error) => "#B3261E",
        _ => throw new ArgumentOutOfRangeException(nameof(tone))
    };
}
