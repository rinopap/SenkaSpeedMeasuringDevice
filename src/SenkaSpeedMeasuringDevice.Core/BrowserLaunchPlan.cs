namespace SenkaSpeedMeasuringDevice.Core;

/// <summary>
/// Defines the application's fixed browser arguments for the two distinct user flows.
/// Authentication preparation must never start a remote debugging endpoint.
/// </summary>
public static class BrowserLaunchPlan
{
    public const string GamePage = "https://play.games.dmm.com/game/kancolle";

    public static IReadOnlyList<string> CreateArguments(bool enableCdp, string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var arguments = new List<string>(enableCdp ? 5 : 3);
        if (enableCdp)
        {
            arguments.Add("--remote-debugging-address=127.0.0.1");
            arguments.Add("--remote-debugging-port=0");
        }
        arguments.Add($"--user-data-dir={profileDirectory}");
        arguments.Add("--new-window");
        arguments.Add(GamePage);
        return arguments;
    }
}
