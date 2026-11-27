namespace SenkaSpeedMeasuringDevice.Capture;

/// <summary>Validates the browser-supplied CDP WebSocket endpoint before opening a local connection.</summary>
public static class CdpEndpointPolicy
{
    public static bool IsAllowedWebSocket(Uri endpoint, int expectedPort, string? expectedTargetId = null) =>
        endpoint.IsAbsoluteUri &&
        endpoint.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) &&
        endpoint.IsLoopback &&
        endpoint.Port == expectedPort &&
        string.IsNullOrEmpty(endpoint.UserInfo) &&
        string.IsNullOrEmpty(endpoint.Query) &&
        string.IsNullOrEmpty(endpoint.Fragment) &&
        endpoint.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal) &&
        endpoint.AbsolutePath.AsSpan("/devtools/page/".Length).IndexOf('/') < 0 &&
        (expectedTargetId is null || endpoint.AbsolutePath.Equals("/devtools/page/" + expectedTargetId, StringComparison.Ordinal));
}
