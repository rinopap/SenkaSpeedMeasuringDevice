using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Capture;

/// <summary>Observes one explicitly selected loopback-CDP page target; it never controls page content.</summary>
public sealed class CdpCaptureSource : ICaptureSource
{
    private const string ApiPath = KancolleCapturePolicy.ApiPath;
    private const string TargetListPath = "json/list";
    private const int MaximumBufferedBytes = CdpCommandPolicy.MaximumBufferedBytes;
    private const int MaximumCdpMessageBytes = 12 * 1024 * 1024;
    private const int MaximumTargetListBytes = 256 * 1024;

    private readonly CdpCaptureOptions _options;
    private readonly GameServer _server;
    private readonly ConcurrentDictionary<string, byte> _observedIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stopSource;
    private Task? _worker;

    public CdpCaptureSource(CdpCaptureOptions options)
    {
        _options = options;
        options.Validate();
        _server = GameServerRegistry.TryGet(options.ServerId, out var server)
            ? server
            : throw new ArgumentOutOfRangeException(nameof(options), "所属サーバーを選択してください。");
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
        using var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3)
        }) { Timeout = TimeSpan.FromSeconds(3) };
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SetStatus("CDP接続先を確認中");
                var target = await FindPageTargetAsync(http, cancellationToken).ConfigureAwait(false);
                var policy = new CdpCommandPolicy();
                await using var connection = new CdpConnection(target.WebSocketUrl, policy);
                await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
                await connection.SendAsync("Page.enable", new { }, cancellationToken).ConfigureAwait(false);
                var frameTree = await connection.SendAsync("Page.getFrameTree", new { }, cancellationToken).ConfigureAwait(false);
                if (!TryGetRootFrame(frameTree, out var frameId, out var frameUrl, out var relatedFrames) ||
                    !KancolleCapturePolicy.IsAllowedGamePage(frameUrl))
                    throw new InvalidOperationException("CDPルートフレームが許可されたゲームページではありません。");
                policy.SetTopFrame(frameId, frameUrl, relatedFrames);
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
            catch (OperationCanceledException)
            {
                SetStatus("CDP接続待機中");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or
                                       WebSocketException or IOException or TimeoutException or ArgumentException or FormatException)
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
        if (response.Content.Headers.ContentLength is > MaximumTargetListBytes)
            throw new InvalidDataException("CDP target list exceeded the local safety limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var targetList = await ReadBoundedAsync(stream, MaximumTargetListBytes, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(targetList);
        var matches = new List<PageTarget>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "page" ||
                !item.TryGetProperty("url", out var url) ||
                !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var pageUri) ||
                !KancolleCapturePolicy.IsAllowedGamePage(pageUri) ||
                !item.TryGetProperty("id", out var targetIdElement) || targetIdElement.GetString() is not { Length: > 0 } targetId ||
                !item.TryGetProperty("webSocketDebuggerUrl", out var webSocketUrl)) continue;
            var uri = new Uri(webSocketUrl.GetString()!);
            if (!CdpEndpointPolicy.IsAllowedWebSocket(uri, _options.DebugPort, targetId))
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

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > maximumBytes) throw new InvalidDataException("Local CDP response exceeded the safety limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryGetRootFrame(JsonElement result, out string frameId, out Uri frameUrl,
        out List<(string FrameId, string? ParentFrameId)> relatedFrames)
    {
        frameId = string.Empty;
        frameUrl = new Uri("about:blank");
        relatedFrames = [];
        if (!result.TryGetProperty("frameTree", out var tree) || !tree.TryGetProperty("frame", out var frame) ||
            !frame.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } parsedId ||
            !frame.TryGetProperty("url", out var url) || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var parsedUrl))
            return false;
        frameId = parsedId;
        frameUrl = parsedUrl;
        CollectFrameIds(tree, null, relatedFrames);
        return true;
    }

    private static void CollectFrameIds(JsonElement tree, string? parentFrameId,
        List<(string FrameId, string? ParentFrameId)> frames)
    {
        if (!tree.TryGetProperty("frame", out var frame) || !frame.TryGetProperty("id", out var id) ||
            id.GetString() is not { Length: > 0 } frameId) return;
        frames.Add((frameId, parentFrameId));
        if (!tree.TryGetProperty("childFrames", out var children) || children.ValueKind != JsonValueKind.Array) return;
        foreach (var child in children.EnumerateArray()) CollectFrameIds(child, frameId, frames);
    }

    private async Task ObserveEventsAsync(CdpConnection connection, CancellationToken cancellationToken)
    {
        var pending = new Dictionary<(string SessionId, string RequestId), long>();
        await foreach (var cdpEvent in connection.Events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var message = cdpEvent.Message;
            if (!message.TryGetProperty("method", out var methodElement) ||
                !message.TryGetProperty("params", out var parameters)) continue;
            var method = methodElement.GetString();
            var sessionId = message.TryGetProperty("sessionId", out var sessionElement)
                ? sessionElement.GetString() ?? string.Empty
                : string.Empty;

            if (sessionId.Length == 0 && method is "Page.frameNavigated" or "Page.navigatedWithinDocument")
                SetStatus(cdpEvent.PageAllowed ? "CDP接続中" : "CDP対象ページ外");
            if (cdpEvent.PageStateChanged)
            {
                if (cdpEvent.PageAllowed)
                {
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
                        filter = new object[] { new { type = "iframe", exclude = false }, new { exclude = true } }
                    }, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await connection.SendAsync("Target.setAutoAttach", new
                    {
                        autoAttach = false,
                        waitForDebuggerOnStart = false,
                        flatten = true
                    }, cancellationToken).ConfigureAwait(false);
                    await connection.SendAsync("Network.disable", new { }, cancellationToken).ConfigureAwait(false);
                }
            }

            if (method == "Target.attachedToTarget" &&
                parameters.TryGetProperty("sessionId", out var childSessionElement) &&
                childSessionElement.GetString() is { Length: > 0 } childSessionId &&
                parameters.TryGetProperty("targetInfo", out var targetInfo) &&
                targetInfo.TryGetProperty("type", out var type) && type.GetString() == "iframe")
            {
                if (!connection.Policy.IsSessionActive(childSessionId)) continue;
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
                if (KancolleCapturePolicy.IsAllowedApi(url, _server) && status is >= 200 and < 300)
                {
                    if (connection.Policy.TryRegisterRequest(sessionId, requestId, cdpEvent.Generation, out var generation))
                    {
                        pending[requestKey] = generation;
                        if (pending.Count > 256)
                        {
                            var oldest = pending.Keys.First();
                            pending.Remove(oldest);
                            connection.Policy.CompleteRequest(oldest.SessionId, oldest.RequestId);
                        }
                    }
                }
            }
            else if (method == "Network.loadingFailed")
            {
                pending.Remove(requestKey);
                connection.Policy.CompleteRequest(sessionId, requestId);
            }
            else if (method == "Network.loadingFinished" && pending.Remove(requestKey, out var generation))
            {
                if (!connection.Policy.IsCurrent(sessionId, requestId, generation)) continue;
                try
                {
                    var result = await connection.SendAsync("Network.getResponseBody", new { requestId }, cancellationToken,
                            string.IsNullOrEmpty(sessionId) ? null : sessionId)
                        .ConfigureAwait(false);
                    if (!connection.Policy.IsCurrent(sessionId, requestId, generation) ||
                        !result.TryGetProperty("body", out var bodyElement) ||
                        !result.TryGetProperty("base64Encoded", out var base64Element)) continue;
                    var text = bodyElement.GetString() ?? string.Empty;
                    var body = base64Element.GetBoolean() ? Convert.FromBase64String(text) : Encoding.UTF8.GetBytes(text);
                    if (body.Length > MaximumBufferedBytes || !connection.Policy.IsCurrent(sessionId, requestId, generation)) continue;
                    var timestamp = DateTimeOffset.UtcNow;
                    var prefix = BitConverter.GetBytes(timestamp.UtcTicks);
                    var eventId = Convert.ToHexString(SHA256.HashData(prefix.Concat(body).ToArray()));
                    if (!_observedIds.TryAdd(eventId, 0)) continue;
                    if (_observedIds.Count > 4096) _observedIds.Clear();
                    connection.Policy.TryExecuteIfCurrent(sessionId, requestId, generation,
                        () => Publish(new ApiResponse(ApiPath, timestamp, body, eventId)));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is InvalidOperationException or JsonException or WebSocketException or
                                           IOException or TimeoutException or FormatException)
                {
                    // A failed read is discarded. No retry or game request is issued.
                }
                finally { connection.Policy.CompleteRequest(sessionId, requestId); }
            }
        }
    }

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
        private readonly ClientWebSocket _socket = CreateSocket();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly System.Threading.Channels.Channel<CdpEvent> _events =
            System.Threading.Channels.Channel.CreateBounded<CdpEvent>(new System.Threading.Channels.BoundedChannelOptions(512)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
        private readonly Uri _endpoint;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly CancellationTokenSource _readerStop = new();
        private Task? _reader;
        private int _nextId;

        public CdpConnection(Uri endpoint, CdpCommandPolicy policy) { _endpoint = endpoint; Policy = policy; }
        public CdpCommandPolicy Policy { get; }
        public System.Threading.Channels.ChannelReader<CdpEvent> Events => _events.Reader;

        private static ClientWebSocket CreateSocket()
        {
            var socket = new ClientWebSocket();
            socket.Options.Proxy = DirectOnlyProxy.Instance;
            return socket;
        }

        public async Task ConnectAsync(CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await _socket.ConnectAsync(_endpoint, timeout.Token).ConfigureAwait(false);
            _reader = ReadLoopAsync(_readerStop.Token);
        }

        public async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken token,
            string? sessionId = null)
        {
            using var parameterDocument = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(parameters));
            if (!Policy.ValidateCommand(method, parameterDocument.RootElement, sessionId))
                throw new InvalidOperationException("CDP command was rejected by the fixed observation policy.");
            var id = Interlocked.Increment(ref _nextId);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            var payload = sessionId is null
                ? JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters })
                : JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters, sessionId });
            using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            commandTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await _sendLock.WaitAsync(commandTimeout.Token).ConfigureAwait(false);
                try { await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, commandTimeout.Token).ConfigureAwait(false); }
                finally { _sendLock.Release(); }
                return await completion.Task.WaitAsync(commandTimeout.Token).ConfigureAwait(false);
            }
            finally { _pending.TryRemove(id, out _); }
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
                        if (message.Length + result.Count > MaximumCdpMessageBytes)
                            throw new InvalidDataException("CDP message exceeded the local safety limit.");
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
                    {
                        var wasPageAllowed = Policy.IsPageAllowed;
                        Policy.ObserveEvent(root);
                        var isPageAllowed = Policy.IsPageAllowed;
                        if (!_events.Writer.TryWrite(new CdpEvent(root.Clone(), Policy.Generation, isPageAllowed,
                                wasPageAllowed != isPageAllowed)))
                        {
                            _socket.Abort();
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or InvalidOperationException or
                                       System.Threading.Channels.ChannelClosedException) { }
            finally
            {
                foreach (var pending in _pending.Values) pending.TrySetException(new IOException("CDP connection closed."));
                _pending.Clear();
                _events.Writer.TryComplete();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var disableTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await SendAsync("Network.disable", new { }, disableTimeout.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or WebSocketException or
                                           TimeoutException or IOException) { }
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "capture stopped", closeTimeout.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
                { _socket.Abort(); }
            }
            _readerStop.Cancel();
            if (_reader is not null)
            {
                try { await _reader.ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or JsonException or InvalidOperationException) { }
            }
            _socket.Dispose();
            _sendLock.Dispose();
            _readerStop.Dispose();
        }

        private sealed class DirectOnlyProxy : IWebProxy
        {
            public static DirectOnlyProxy Instance { get; } = new();
            public ICredentials? Credentials { get; set; }
            public Uri GetProxy(Uri destination) => destination;
            public bool IsBypassed(Uri host) => true;
        }

        public sealed record CdpEvent(JsonElement Message, long Generation, bool PageAllowed, bool PageStateChanged);
    }
}

