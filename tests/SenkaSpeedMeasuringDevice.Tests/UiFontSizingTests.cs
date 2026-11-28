using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class UiFontSizingTests
{
    [Fact]
    public void ConvertsQtPointSizeToWpfDipWithoutChangingStoredPreference()
    {
        Assert.Equal(16, UiFontSizing.QtPointSizeToWpfDip(12), 8);
        Assert.Equal(18.66666667, UiFontSizing.DisplayTextDip(12), 7);
    }

    [Fact]
    public void WindowSizingSampleReservesSpaceForSevenDigitExpRate()
    {
        Assert.Contains("1,234,567", UiFontSizing.MaximumExpRateWidthSample);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidPointSizes(double size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => UiFontSizing.QtPointSizeToWpfDip(size));
}
