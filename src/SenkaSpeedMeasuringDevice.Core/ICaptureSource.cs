namespace SenkaSpeedMeasuringDevice.Core;

/// <summary>Receives response observations without controlling the game connection.</summary>
public interface ICaptureSource : IAsyncDisposable
{
    string Name { get; }

    bool IsRunning { get; }

    event EventHandler<ApiResponse>? ResponseReceived;

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
