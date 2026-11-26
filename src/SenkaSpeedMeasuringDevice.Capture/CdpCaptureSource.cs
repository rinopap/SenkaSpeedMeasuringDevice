using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Capture;

/// <summary>Observes one explicitly selected loopback-CDP page target; it never controls page content.</summary>
public sealed class CdpCaptureSource : ICaptureSource
{
    private const string ApiPath = "/kcsapi/api_port/port";
    private const string TargetListPath = "json/list";
    private const int MaximumBufferedBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> AllowedCommands = [
        "Network.enable", "Network.disable", "Network.getResponseBody", "Target.setAutoAttach"];

    private readonly CdpCaptureOptions _options;
    private readonly ConcurrentDictionary<string, byte> _observedIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stopSource;
    private Task? _worker;

    public CdpCaptureSource(CdpCaptureOptions options)
    {
        _options = options;
        options.Validate();
    }

    public string Name => CaptureModule.CdpSourceName;
    public bool IsRunning => _stopSource is { IsCancellationRequested: false };
    public event EventHandler<ApiResponse>? ResponseReceived;
    public event EventHandler<string>? StatusChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsRunning) return Task.CompletedTask;
        var cts = new CancellationTokenSource();
        _stopSource = cts;
        _worker = RunAsync(cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var cts = _stopSource;
        if (cts is null) return;
        _stopSource = null;
        cts.Cancel();
        try
        {
            if (_worker is not null) await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally
        {
            _worker = null;
            cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SetStatus("CDP接続先を確認中");
                var target = await FindPageTargetAsync(http, cancellationToken).ConfigureAwait(false);
                await using var connection = new CdpConnection(target.WebSocketUrl);
                await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
                await connection.SendAsync("Network.enable", new
                {
                    maxTotalBufferSize = MaximumBufferedBytes,
                    maxResourceBufferSize = MaximumBufferedBytes
                }, cancellationToken).ConfigureAwait(false);
                await connection.SendAsync("Target.setAutoAttach", new
                {
                    autoAttach = true,
                    waitForDebuggerOnStart = false,
                    flatten = true,
                    filter = new object[]
                    {
                        new { type = "iframe", exclude = false },
                        new { exclude = true }
                    }
                }, cancellationToken).ConfigureAwait(false);
                SetStatus("CDP接続中");
                await ObserveEventsAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or
                                       WebSocketException or IOException or TimeoutException or ArgumentException)
            {
                SetStatus("CDP接続待機中");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
        SetStatus("CDP停止");
    }

    private async Task<PageTarget> FindPageTargetAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var endpoint = new Uri($"http://127.0.0.1:{_options.DebugPort}/{TargetListPath}");
        using var response = await http.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var wanted = new Uri(_options.PageUrl).AbsoluteUri;
        var matches = new List<PageTarget>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "page" ||
                !item.TryGetProperty("url", out var url) || !string.Equals(url.GetString(), wanted, StringComparison.Ordinal) ||
                !item.TryGetProperty("webSocketDebuggerUrl", out var webSocketUrl)) continue;
            var uri = new Uri(webSocketUrl.GetString()!);
            if (uri.Scheme != "ws" || !uri.IsLoopback)
                throw new InvalidOperationException("CDP WebSocketはloopbackに限定してください。");
            matches.Add(new PageTarget(uri));
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException("指定URLのページタブがまだありません。"),
            _ => throw new InvalidOperationException("指定URLに一致するタブが複数あります。対象を一意にしてください。")
        };
    }

    private async Task ObserveEventsAsync(CdpConnection connection, CancellationToken cancellationToken)
    {
        var pending = new Dictionary<(string SessionId, string RequestId), Uri>();
        await foreach (var message in connection.Events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!message.TryGetProperty("method", out var methodElement) ||
                !message.TryGetProperty("params", out var parameters)) continue;
            var method = methodElement.GetString();
            var sessionId = message.TryGetProperty("sessionId", out var sessionElement)
                ? sessionElement.GetString() ?? string.Empty
                : string.Empty;

            if (method == "Target.attachedToTarget" &&
                parameters.TryGetProperty("sessionId", out var childSessionElement) &&
                childSessionElement.GetString() is { Length: > 0 } childSessionId &&
                parameters.TryGetProperty("targetInfo", out var targetInfo) &&
                targetInfo.TryGetProperty("type", out var type) && type.GetString() == "iframe")
            {
                await connection.SendAsync("Network.enable", new
                {
                    maxTotalBufferSize = MaximumBufferedBytes,
                    maxResourceBufferSize = MaximumBufferedBytes
                }, cancellationToken, childSessionId).ConfigureAwait(false);
                await connection.SendAsync("Target.setAutoAttach", new
                {
                    autoAttach = true,
                    waitForDebuggerOnStart = false,
                    flatten = true,
                    filter = new object[]
                    {
                        new { type = "iframe", exclude = false },
                        new { exclude = true }
                    }
                }, cancellationToken, childSessionId).ConfigureAwait(false);
                continue;
            }
            if (method == "Target.detachedFromTarget") continue;

            var requestId = parameters.TryGetProperty("requestId", out var idElement) ? idElement.GetString() : null;
            if (requestId is null) continue;
            var requestKey = (sessionId, requestId);

            if (method == "Network.responseReceived" && parameters.TryGetProperty("response", out var response))
            {
                var urlText = response.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url)) continue;
                var status = response.TryGetProperty("status", out var statusElement) ? statusElement.GetDouble() : 0;
                if (IsTargetApi(url) && status is >= 200 and < 300)
                {
                    pending[requestKey] = url;
                    if (pending.Count > 256) pending.Remove(pending.Keys.First());
                }
            }
            else if (method == "Network.loadingFailed")
            {
                pending.Remove(requestKey);
            }
            else if (method == "Network.loadingFinished" && pending.Remove(requestKey, out _))
            {
                var result = await connection.SendAsync("Network.getResponseBody", new { requestId }, cancellationToken,
                        string.IsNullOrEmpty(sessionId) ? null : sessionId)
                    .ConfigureAwait(false);
                if (!result.TryGetProperty("body", out var bodyElement) || !result.TryGetProperty("base64Encoded", out var base64Element))
                    continue;
                var text = bodyElement.GetString() ?? string.Empty;
                var body = base64Element.GetBoolean() ? Convert.FromBase64String(text) : Encoding.UTF8.GetBytes(text);
                if (body.Length > MaximumBufferedBytes) continue;
                var timestamp = DateTimeOffset.UtcNow;
                var prefix = BitConverter.GetBytes(timestamp.UtcTicks);
                var eventId = Convert.ToHexString(SHA256.HashData(prefix.Concat(body).ToArray()));
                if (!_observedIds.TryAdd(eventId, 0)) continue;
                if (_observedIds.Count > 4096) _observedIds.Clear();
                Publish(new ApiResponse(ApiPath, timestamp, body, eventId));
            }
        }
    }

    private bool IsTargetApi(Uri url) =>
        (url.Scheme is "http" or "https") && url.AbsolutePath == ApiPath &&
        _options.ApiHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);

    private void Publish(ApiResponse response)
    {
        if (ResponseReceived is not { } handlers) return;
        foreach (EventHandler<ApiResponse> handler in handlers.GetInvocationList())
        {
            try { handler(this, response); }
            catch { /* An observer error must not alter the browser connection. */ }
        }
    }

    private void SetStatus(string status)
    {
        if (StatusChanged is not { } handlers) return;
        foreach (EventHandler<string> handler in handlers.GetInvocationList())
        {
            try { handler(this, status); }
            catch { }
        }
    }

    private sealed record PageTarget(Uri WebSocketUrl);

    private sealed class CdpConnection : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly System.Threading.Channels.Channel<JsonElement> _events =
            System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
        private readonly Uri _endpoint;
        private readonly CancellationTokenSource _readerStop = new();
        private Task? _reader;
        private int _nextId;

        public CdpConnection(Uri endpoint) => _endpoint = endpoint;
        public System.Threading.Channels.ChannelReader<JsonElement> Events => _events.Reader;

        public async Task ConnectAsync(CancellationToken token)
        {
            await _socket.ConnectAsync(_endpoint, token).ConfigureAwait(false);
            _reader = ReadLoopAsync(_readerStop.Token);
        }

        public async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken token,
            string? sessionId = null)
        {
            if (!AllowedCommands.Contains(method)) throw new InvalidOperationException($"Blocked CDP command: {method}");
            var id = Interlocked.Increment(ref _nextId);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            var payload = sessionId is null
                ? JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters })
                : JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters, sessionId });
            await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (!token.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        await message.WriteAsync(buffer.AsMemory(0, result.Count), token).ConfigureAwait(false);
                    } while (!result.EndOfMessage);

                    using var document = JsonDocument.Parse(message.ToArray());
                    var root = document.RootElement;
                    if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id) &&
                        _pending.TryRemove(id, out var completion))
                    {
                        if (root.TryGetProperty("error", out var error))
                            completion.TrySetException(new InvalidOperationException(error.ToString()));
                        else if (root.TryGetProperty("result", out var response))
                            completion.TrySetResult(response.Clone());
                        else
                            completion.TrySetException(new JsonException("CDP response has no result."));
                    }
                    else
                        await _events.Writer.WriteAsync(root.Clone(), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (WebSocketException) { }
            finally { _events.Writer.TryComplete(); }
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var disableTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await SendAsync("Network.disable", new { }, disableTimeout.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or WebSocketException or TimeoutException) { }
                try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "capture stopped", CancellationToken.None).ConfigureAwait(false); }
                catch (WebSocketException) { _socket.Abort(); }
            }
            _readerStop.Cancel();
            if (_reader is not null)
            {
                try { await _reader.ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            }
            _socket.Dispose();
            _readerStop.Dispose();
        }
    }
}

