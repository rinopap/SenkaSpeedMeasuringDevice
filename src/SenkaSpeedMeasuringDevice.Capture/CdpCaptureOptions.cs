namespace SenkaSpeedMeasuringDevice.Capture;

public sealed record CdpCaptureOptions(int DebugPort, string PageUrl, IReadOnlySet<string> ApiHosts)
{
    public void Validate()
    {
        if (DebugPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(DebugPort));
        if (!Uri.TryCreate(PageUrl, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(page.UserInfo) || !string.IsNullOrEmpty(page.Fragment))
            throw new ArgumentException("CDP対象ページURLはHTTP(S)の完全なURLで指定してください。", nameof(PageUrl));
        if (ApiHosts.Count == 0 || ApiHosts.Any(host => Uri.CheckHostName(host) == UriHostNameType.Unknown))
            throw new ArgumentException("対象APIホストを1つ以上指定してください。", nameof(ApiHosts));
    }
}

