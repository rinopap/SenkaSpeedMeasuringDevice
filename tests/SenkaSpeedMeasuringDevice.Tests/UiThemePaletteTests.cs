using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class UiThemePaletteTests
{
    [Fact]
    public void ProvidesFlatDarkPaletteWithReadableSelectedTab()
    {
        var palette = UiThemePalette.For(darkMode: true);
        Assert.Equal("#202124", palette.Background);
        Assert.Equal("#28547D", palette.SelectedTab);
        Assert.Equal("#F1F3F4", palette.SelectedTabForeground);
        Assert.Equal("#E8EAED", palette.Foreground);
        Assert.NotEqual(palette.Background, palette.Border);
    }

    [Fact]
    public void ProvidesReadableLightPalette()
    {
        var palette = UiThemePalette.For(darkMode: false);
        Assert.Equal("#F0F0F0", palette.Background);
        Assert.Equal("#202124", palette.Foreground);
        Assert.Equal("#174EA6", palette.SelectedTabForeground);
    }
}
