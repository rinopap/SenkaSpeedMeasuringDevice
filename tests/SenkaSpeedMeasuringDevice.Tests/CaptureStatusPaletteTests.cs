using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class CaptureStatusPaletteTests
{
    [Theory]
    [InlineData(false, CaptureStatusTone.Normal, "#202124")]
    [InlineData(false, CaptureStatusTone.Info, "#3C4043")]
    [InlineData(false, CaptureStatusTone.Warning, "#8A5A00")]
    [InlineData(false, CaptureStatusTone.Error, "#B3261E")]
    [InlineData(true, CaptureStatusTone.Normal, "#E8EAED")]
    [InlineData(true, CaptureStatusTone.Info, "#DADCE0")]
    [InlineData(true, CaptureStatusTone.Warning, "#FDD663")]
    [InlineData(true, CaptureStatusTone.Error, "#F28B82")]
    public void ProvidesThemeSpecificStatusForeground(bool darkMode, CaptureStatusTone tone, string expected) =>
        Assert.Equal(expected, CaptureStatusPalette.ForegroundHex(darkMode, tone));
}
