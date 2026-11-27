using SenkaSpeedMeasuringDevice.Capture;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class FileCaptureSourceTests
{
    [Fact]
    public async Task ObservesOnlyTargetJsonAndCanRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SenkaCaptureTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = new FileCaptureSource(directory);
        var received = new TaskCompletionSource<ApiResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ResponseReceived += (_, response) => received.TrySetResult(response);
        try
        {
            await source.StartAsync(CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(directory, "unrelated.json"), "{}");
            await Task.Delay(250);
            Assert.False(received.Task.IsCompleted);

            var target = Path.Combine(directory, "api_port@port_1.json");
            await File.WriteAllTextAsync(target, Json(123));
            var first = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(123, ApiResponseParser.Parse(first)?.AdmiralExperience);
            Assert.Equal("/kcsapi/api_port/port", first.ApiPath);

            await source.StopAsync(CancellationToken.None);
            Assert.False(source.IsRunning);
            var secondReceived = new TaskCompletionSource<ApiResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.ResponseReceived += (_, response) => secondReceived.TrySetResult(response);
            await source.StartAsync(CancellationToken.None);
            await File.WriteAllTextAsync(target, Json(456));
            var second = await secondReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(456, ApiResponseParser.Parse(second)?.AdmiralExperience);
        }
        finally
        {
            await source.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DoesNotReplayFilesThatExistBeforeCaptureStarts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SenkaCaptureTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = new FileCaptureSource(directory);
        var count = 0;
        source.ResponseReceived += (_, _) => Interlocked.Increment(ref count);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "api_port@port_old.json"), Json(5));
            await source.StartAsync(CancellationToken.None);
            await Task.Delay(300);
            Assert.Equal(0, Volatile.Read(ref count));
        }
        finally
        {
            await source.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PicksUpJsonThatWasInitiallyWrittenPartially()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SenkaCaptureTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = new FileCaptureSource(directory);
        var received = new TaskCompletionSource<ApiResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ResponseReceived += (_, response) => received.TrySetResult(response);
        try
        {
            await source.StartAsync(CancellationToken.None);
            var target = Path.Combine(directory, "api_port@port_partial.json");
            await File.WriteAllTextAsync(target, "svdata={\"api_result\":1,\"api_data\":");
            await Task.Delay(350);
            await File.AppendAllTextAsync(target, "{\"api_basic\":{\"api_experience\":789}}}");
            var response = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(789, ApiResponseParser.Parse(response)?.AdmiralExperience);
        }
        finally
        {
            await source.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Json(long experience) =>
        $"{{\"api_result\":1,\"api_data\":{{\"api_basic\":{{\"api_experience\":{experience}}},\"api_ship\":[{{\"api_exp\":[100,0]}}]}}}}";
}
