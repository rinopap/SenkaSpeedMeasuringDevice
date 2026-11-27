using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Capture;

public sealed record CdpCaptureOptions(int DebugPort, int ServerId, string? ExpectedProcessPath = null)
{
    public void Validate()
    {
        if (DebugPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(DebugPort));
        if (ExpectedProcessPath is not null && (!Path.IsPathFullyQualified(ExpectedProcessPath) ||
                                                !string.Equals(ExpectedProcessPath, Path.GetFullPath(ExpectedProcessPath), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("接続先ブラウザの実行ファイルパスが不正です。", nameof(ExpectedProcessPath));
        if (!GameServerRegistry.TryGet(ServerId, out _))
            throw new ArgumentOutOfRangeException(nameof(ServerId), "所属サーバーを選択してください。");
    }
}
