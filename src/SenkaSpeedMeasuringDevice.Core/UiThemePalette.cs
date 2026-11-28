namespace SenkaSpeedMeasuringDevice.Core;

public sealed record UiThemePalette(
    string Background,
    string Surface,
    string Foreground,
    string MutedForeground,
    string Border,
    string SelectedTab,
    string SelectedTabForeground,
    string Hover,
    string Focus)
{
    public static UiThemePalette For(bool darkMode) => darkMode
        ? new UiThemePalette("#202124", "#282A2D", "#E8EAED", "#9AA0A6", "#414247",
            "#28547D", "#F1F3F4", "#303134", "#8AB4F8")
        : new UiThemePalette("#F0F0F0", "#FFFFFF", "#202124", "#5F6368", "#DADCE0",
            "#D2E3FC", "#174EA6", "#E8F0FE", "#1A73E8");
}
