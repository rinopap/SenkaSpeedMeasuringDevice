namespace SenkaSpeedMeasuringDevice.Core;

public static class KancolleCapturePolicy
{
    public const string GamePageUrl = "https://play.games.dmm.com/game/kancolle";
    public const string ApiPath = "/kcsapi/api_port/port";

    public static bool IsAllowedGamePage(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals("play.games.dmm.com", StringComparison.OrdinalIgnoreCase) &&
        uri.Port == 443 &&
        uri.AbsolutePath.Equals("/game/kancolle", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    public static bool IsAllowedApi(Uri uri, GameServer server) =>
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == 443 &&
        uri.Host.Equals(server.ApiHost, StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Equals(ApiPath, StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment);

    public static bool IsAllowedGameFrame(Uri uri, GameServer server) =>
        uri.IsAbsoluteUri &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == 443 &&
        uri.Host.Equals(server.ApiHost, StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Equals("/kcs2/index.php", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment);
}
