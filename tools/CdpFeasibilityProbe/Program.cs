using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace CdpFeasibilityProbe;

internal static class Program
{
    private static readonly HashSet<string> AllowedMethods = [
        "Network.enable", "Network.disable", "Network.getResponseBody"];

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ProbeOptions.Parse(args);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var page = await SelectPageAsync(client, options);
            await using var cdp = new CdpConnection(page.WebSocketUrl);
            using var timeout = new CancellationTokenSource(options.Timeout);
            await cdp.ConnectAsync(timeout.Token);
            await cdp.SendAsync("Network.enable", new { }, timeout.Token);
            Console.WriteLine("CDP接続済み。許可コマンド: Network.enable / Network.getResponseBody / Network.disable");

            var matchingResponses = new Dictionary<string, bool>(StringComparer.Ordinal);
            try
            {
                await foreach (var message in cdp.Events.ReadAllAsync(timeout.Token))
                {
                    if (!message.TryGetProperty("method", out var methodValue)) continue;
                    var method = methodValue.GetString();
                    if (method == "Network.responseReceived")
                    {
                        var parameters = message.GetProperty("params");
                        var requestId = parameters.GetProperty("requestId").GetString();
                        var response = parameters.GetProperty("response");
                        var url = new Uri(response.GetProperty("url").GetString()!);
                        var status = response.GetProperty("status").GetDouble();
                        if (requestId is not null && Matches(url, options) && status is >= 200 and < 300)
                            matchingResponses[requestId] = true;
                    }
                    else if (method == "Network.loadingFailed")
                    {
                        matchingResponses.Remove(message.GetProperty("params").GetProperty("requestId").GetString()!);
                    }
                    else if (method == "Network.loadingFinished")
                    {
                        var requestId = message.GetProperty("params").GetProperty("requestId").GetString();
                        if (requestId is null || !matchingResponses.Remove(requestId)) continue;
                        var result = await cdp.SendAsync("Network.getResponseBody", new { requestId }, timeout.Token);
                        var bodyText = result.GetProperty("body").GetString() ?? string.Empty;
                        var body = result.GetProperty("base64Encoded").GetBoolean()
                            ? Convert.FromBase64String(bodyText)
                            : Encoding.UTF8.GetBytes(bodyText);
                        var hash = Convert.ToHexString(SHA256.HashData(body));
                        Console.WriteLine($"対象レスポンス取得: bytes={body.Length}, sha256={hash}");
                        return 0;
                    }
                }
            }
            finally
            {
                try { await cdp.SendAsync("Network.disable", new { }, CancellationToken.None); }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or WebSocketException or TimeoutException) { }
            }
            Console.Error.WriteLine("タイムアウトまで対象レスポンスを検出しませんでした。");
            return 2;
        }
        catch (Exception ex) when (ex is ArgumentException or HttpRequestException or JsonException or
                                   InvalidOperationException or OperationCanceledException or WebSocketException or
                                   SocketException or FormatException)
        {
            Console.Error.WriteLine($"CDP probe failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<PageTarget> SelectPageAsync(HttpClient client, ProbeOptions options)
    {
        var endpoint = new Uri($"http://127.0.0.1:{options.Port}/json/list");
        using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);

        var matches = new List<PageTarget>();
        foreach (var target in document.RootElement.EnumerateArray())
        {
            if (!target.TryGetProperty("type", out var type) || type.GetString() != "page" ||
                !target.TryGetProperty("url", out var urlElement) || urlElement.GetString() != options.PageUrl)
                continue;
            if (!target.TryGetProperty("webSocketDebuggerUrl", out var websocket)) continue;
            var wsUri = new Uri(websocket.GetString()!);
            if (!wsUri.IsLoopback || wsUri.Scheme != "ws")
                throw new InvalidOperationException("CDP WebSocketはloopback上のws://接続に限定しています。");
            matches.Add(new PageTarget(wsUri));
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException("指定URLと完全一致するページタブがありません。"),
            _ => throw new InvalidOperationException("指定URLのページタブが複数あります。対象を1つにしてください。")
        };
    }

    private static bool Matches(Uri url, ProbeOptions options) =>
        (url.Scheme is "http" or "https") &&
        string.Equals(url.Host, options.ApiHost, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(url.AbsolutePath, options.ApiPath, StringComparison.Ordinal);

    private sealed record PageTarget(Uri WebSocketUrl);

    private sealed record ProbeOptions(int Port, string PageUrl, string ApiHost, string ApiPath, TimeSpan Timeout)
    {
        public static ProbeOptions Parse(string[] args)
        {
            string Required(string name)
            {
                var index = Array.IndexOf(args, name);
                if (index < 0 || index + 1 >= args.Length) throw new ArgumentException($"Required: {name} <value>");
                return args[index + 1];
            }

            if (!int.TryParse(Required("--port"), out var port) || port is < 1 or > 65535)
                throw new ArgumentException("--port must be in 1..65535.");
            var pageUrl = new Uri(Required("--page-url"));
            if (pageUrl.Scheme is not ("http" or "https")) throw new ArgumentException("--page-url must use HTTP(S).");
            var host = Required("--api-host");
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown) throw new ArgumentException("--api-host is invalid.");
            var apiPath = Required("--api-path");
            if (!apiPath.StartsWith('/') || apiPath.Contains('?') || apiPath.Contains('#'))
                throw new ArgumentException("--api-path must be an exact path without query or fragment.");
            return new ProbeOptions(port, pageUrl.AbsoluteUri, host, apiPath, TimeSpan.FromMinutes(2));
        }
    }

    private sealed class CdpConnection : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        private int _nextId;
        private Task? _reader;
        private readonly Uri _endpoint;

        public CdpConnection(Uri endpoint) => _endpoint = endpoint;
        public ChannelReader<JsonElement> Events => _events.Reader;

        public async Task ConnectAsync(CancellationToken token)
        {
            await _socket.ConnectAsync(_endpoint, token);
            _reader = ReadLoopAsync(token);
        }

        public async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken token)
        {
            if (!AllowedMethods.Contains(method)) throw new InvalidOperationException($"Blocked CDP command: {method}");
            var id = Interlocked.Increment(ref _nextId);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = completion;
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
            await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, token);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (!token.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    var message = new ArrayBufferWriter<byte>();
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(buffer.AsMemory(), token);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        message.Write(buffer.AsSpan(0, result.Count));
                    } while (!result.EndOfMessage);

                    using var document = JsonDocument.Parse(message.WrittenMemory);
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
                        await _events.Writer.WriteAsync(root.Clone(), token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { _events.Writer.TryComplete(); }
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe complete", CancellationToken.None); }
                catch (WebSocketException) { _socket.Abort(); }
            }
            if (_reader is not null)
            {
                try { await _reader; }
                catch (OperationCanceledException) { }
            }
            _socket.Dispose();
        }
    }
}
