using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SenkaSpeedMeasuringDevice.Capture;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class CdpCaptureSyntheticTests
{
    [Fact]
    public async Task CapturesOnlySelectedServerResponsesOnTheCurrentGamePage()
    {
        Assert.True(GameServerRegistry.TryGet(20, out var server));
        await using var fixture = new LocalCdpFixture();
        var source = new CdpCaptureSource(new CdpCaptureOptions(fixture.Port, server.Id));
        var captured = new TaskCompletionSource<ApiResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ResponseReceived += (_, response) => captured.TrySetResult(response);

        await source.StartAsync(CancellationToken.None);
        try
        {
            await fixture.WaitForConnectionAsync(TimeSpan.FromSeconds(5));
            await fixture.WaitForCommandAsync("Target.setAutoAttach", TimeSpan.FromSeconds(5));

            await fixture.SendEventAsync("Page.frameNavigated", new
            {
                frame = new { id = "main-frame", url = "https://example.invalid/" }
            });
            await fixture.SendEventAsync("Network.responseReceived", Response("blocked-off-page", "https://" + server.ApiHost + KancolleCapturePolicy.ApiPath));
            await fixture.SendEventAsync("Network.loadingFinished", new { requestId = "blocked-off-page" });

            await fixture.SendEventAsync("Page.navigatedWithinDocument", new
            {
                frameId = "main-frame", url = KancolleCapturePolicy.GamePageUrl
            });
            await fixture.SendEventAsync("Network.responseReceived", Response("wrong-host", "https://w01y.kancolle-server.com" + KancolleCapturePolicy.ApiPath));
            await fixture.SendEventAsync("Network.loadingFinished", new { requestId = "wrong-host" });

            await fixture.SendEventAsync("Network.responseReceived", Response("accepted", "https://" + server.ApiHost + KancolleCapturePolicy.ApiPath));
            await fixture.SendEventAsync("Network.loadingFinished", new { requestId = "accepted" });
            var response = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(KancolleCapturePolicy.ApiPath, response.ApiPath);
            Assert.Equal("synthetic response body", Encoding.UTF8.GetString(response.ResponseBody.Span));

            await fixture.SendEventAsync("Page.frameAttached", new { frameId = "oopif-frame", parentFrameId = "main-frame" });
            await fixture.SendEventAsync("Target.attachedToTarget", new
            {
                sessionId = "oopif-session",
                targetInfo = new { type = "iframe", parentFrameId = "main-frame" }
            });
            await fixture.WaitForCommandAsync("Network.enable", "oopif-session", TimeSpan.FromSeconds(5));
            await fixture.SendEventAsync("Network.responseReceived", Response("iframe-accepted", "https://" + server.ApiHost + KancolleCapturePolicy.ApiPath), "oopif-session");
            await fixture.SendEventAsync("Network.loadingFinished", new { requestId = "iframe-accepted" }, "oopif-session");
            await WaitUntilAsync(() => fixture.Commands.Any(command => command.Method == "Network.getResponseBody" &&
                command.SessionId == "oopif-session"), TimeSpan.FromSeconds(5));

            await fixture.SendEventAsync("Target.detachedFromTarget", new { sessionId = "oopif-session" });
            await fixture.SendEventAsync("Network.responseReceived", Response("detached-rejected", "https://" + server.ApiHost + KancolleCapturePolicy.ApiPath), "oopif-session");
            await fixture.SendEventAsync("Network.loadingFinished", new { requestId = "detached-rejected" }, "oopif-session");

            await Task.Delay(150);
            var methods = fixture.Commands.Select(command => command.Method).ToArray();
            Assert.Equal(2, methods.Count(method => method == "Network.getResponseBody"));
            Assert.True(methods.Count(method => method == "Target.setAutoAttach") >= 3);
            Assert.True(methods.Count(method => method == "Network.disable") >= 1, string.Join(",", methods));
            Assert.All(methods, method => Assert.Contains(method, new[]
            {
                "Page.enable", "Page.getFrameTree", "Network.enable", "Target.setAutoAttach",
                "Network.getResponseBody", "Network.disable"
            }));
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    private static object Response(string requestId, string url) => new
    {
        requestId,
        response = new { url, status = 200 }
    };

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!predicate()) await Task.Delay(10, cancellation.Token);
    }

    private sealed class LocalCdpFixture : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource<WebSocket> _socketReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly Task _acceptLoop;
        public int Port { get; }
        public ConcurrentQueue<(string Method, string? SessionId, JsonElement Parameters)> Commands { get; } = new();

        public LocalCdpFixture()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            Port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _acceptLoop = AcceptLoopAsync();
        }

        public async Task WaitForConnectionAsync(TimeSpan timeout) =>
            _ = await _socketReady.Task.WaitAsync(timeout);

        public async Task WaitForCommandAsync(string method, TimeSpan timeout) => await WaitForCommandAsync(method, null, timeout);

        public async Task WaitForCommandAsync(string method, string? sessionId, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                var found = Commands.Any(command => command.Method == method && command.SessionId == sessionId);
                if (found) return;
                await Task.Delay(10, cancellation.Token);
            }
        }

        public async Task SendEventAsync(string method, object parameters, string? sessionId = null)
        {
            var socket = await _socketReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { method, @params = parameters, sessionId });
            await _sendGate.WaitAsync();
            try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { _sendGate.Release(); }
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync();
                    if (context.Request.Url?.AbsolutePath == "/json/list")
                    {
                        var target = new[]
                        {
                            new
                            {
                                type = "page",
                                id = "synthetic",
                                url = KancolleCapturePolicy.GamePageUrl,
                                webSocketDebuggerUrl = $"ws://127.0.0.1:{Port}/devtools/page/synthetic"
                            }
                        };
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(target);
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes);
                        context.Response.Close();
                    }
                    else if (context.Request.IsWebSocketRequest)
                    {
                        var accepted = await context.AcceptWebSocketAsync(null);
                        _socketReady.TrySetResult(accepted.WebSocket);
                        await HandleSocketAsync(accepted.WebSocket);
                    }
                    else
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                    }
                }
            }
            catch (HttpListenerException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }

        private async Task HandleSocketAsync(WebSocket socket)
        {
            var buffer = new byte[64 * 1024];
            while (!_stop.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), _stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    await message.WriteAsync(buffer.AsMemory(0, result.Count), _stop.Token);
                } while (!result.EndOfMessage);

                using var document = JsonDocument.Parse(message.ToArray());
                var root = document.RootElement;
                var id = root.GetProperty("id").GetInt32();
                var method = root.GetProperty("method").GetString()!;
                var parameters = root.GetProperty("params").Clone();
                var sessionId = root.TryGetProperty("sessionId", out var sessionElement) ? sessionElement.GetString() : null;
                Commands.Enqueue((method, sessionId, parameters));
                object response = method switch
                {
                    "Page.getFrameTree" => new
                    {
                        frameTree = new { frame = new { id = "main-frame", url = KancolleCapturePolicy.GamePageUrl } }
                    },
                    "Network.getResponseBody" => new
                    {
                        body = Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic response body")),
                        base64Encoded = true
                    },
                    _ => new { }
                };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, result = response });
                await _sendGate.WaitAsync(_stop.Token);
                try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, _stop.Token); }
                finally { _sendGate.Release(); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_listener.IsListening) _listener.Stop();
            if (_socketReady.Task.IsCompletedSuccessfully)
            {
                var socket = _socketReady.Task.Result;
                try { socket.Abort(); } catch (ObjectDisposedException) { }
                socket.Dispose();
            }
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or WebSocketException or HttpListenerException) { }
            _sendGate.Dispose();
            _stop.Dispose();
            _listener.Close();
        }
    }
}

