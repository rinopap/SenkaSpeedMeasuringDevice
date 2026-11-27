using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Capture;

public sealed record CdpCaptureOptions(int DebugPort, int ServerId)
{
    public void Validate()
    {
        if (DebugPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(DebugPort));
        if (!GameServerRegistry.TryGet(ServerId, out _))
            throw new ArgumentOutOfRangeException(nameof(ServerId), "所属サーバーを選択してください。");
    }
}
