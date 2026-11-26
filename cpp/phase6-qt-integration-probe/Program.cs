using System.Net;
using System.Net.Sockets;
using System.Text;

static int ReadProxyPort(string[] args)
{
    var index = Array.IndexOf(args, "--proxy-port");
    if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var port) || port is < 1 or > 65535)
        throw new ArgumentException("Usage: Phase6QtIntegrationProbe --proxy-port PORT");
    return port;
}

var proxyPort = ReadProxyPort(args);
var payload = Encoding.ASCII.GetBytes("local_phase6_probe=1&api_test=only");
var responseBody = Encoding.UTF8.GetBytes("svdata={\"api_result\":1,\"api_data\":{\"api_basic\":{\"api_experience\":234567},\"api_ship\":[{\"api_exp\":[3210000,0,100]}]}}");

using var portFinder = new TcpListener(IPAddress.Loopback, 0);
portFinder.Start();
var originPort = ((IPEndPoint)portFinder.LocalEndpoint).Port;
portFinder.Stop();

using var origin = new TcpListener(IPAddress.Loopback, originPort);
origin.Start();
var originRequestCount = 0;
var originTask = Task.Run(async () =>
{
    using var tcp = await origin.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
    using var stream = tcp.GetStream();
    using var received = new MemoryStream();
    var headerEnd = -1;
    var tail = new byte[4];
    while (headerEnd < 0)
    {
        var one = new byte[1];
        if (await stream.ReadAsync(one) == 0) throw new IOException("Origin request ended before headers completed");
        received.WriteByte(one[0]);
        for (var i = 0; i < 3; i++) tail[i] = tail[i + 1];
        tail[3] = one[0];
        if (tail.SequenceEqual(new byte[] { 13, 10, 13, 10 })) headerEnd = checked((int)received.Length);
    }
    var headers = Encoding.ASCII.GetString(received.ToArray(), 0, headerEnd);
    var requestLine = headers.Split("\r\n", StringSplitOptions.None)[0].Split(' ', 3);
    var contentLength = 0;
    foreach (var line in headers.Split("\r\n"))
        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            contentLength = int.Parse(line.Split(':', 2)[1].Trim());
    while (received.Length - headerEnd < contentLength)
    {
        var chunk = new byte[Math.Min(4096, contentLength - checked((int)(received.Length - headerEnd)))];
        var count = await stream.ReadAsync(chunk);
        if (count == 0) throw new IOException("Origin request body ended early");
        received.Write(chunk, 0, count);
    }
    var receivedBody = received.ToArray().AsSpan(headerEnd, contentLength).ToArray();
    Interlocked.Increment(ref originRequestCount);
    var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {responseBody.Length}\r\nConnection: close\r\n\r\n").Concat(responseBody).ToArray();
    await stream.WriteAsync(response);
    await stream.FlushAsync();
    return (method: requestLine[0], target: requestLine[1], receivedBody);
});

try
{
    var handler = new HttpClientHandler
    {
        Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
        UseProxy = true,
    };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    using var request = new HttpRequestMessage(HttpMethod.Post,
        $"http://127.0.0.1:{originPort}/kcsapi/api_port/port?check=qt_integration")
    {
        Content = new ByteArrayContent(payload),
    };
    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
    using var response = await client.SendAsync(request);
    var receivedResponse = await response.Content.ReadAsByteArrayAsync();
    var receivedRequest = await originTask;
    var requestUnchanged = receivedRequest.method == "POST" &&
        receivedRequest.target == "/kcsapi/api_port/port?check=qt_integration" &&
        receivedRequest.receivedBody.SequenceEqual(payload);
    var responseUnchanged = response.StatusCode == HttpStatusCode.OK && receivedResponse.SequenceEqual(responseBody);
    Console.WriteLine($"request_method_target_body_unchanged={requestUnchanged}");
    Console.WriteLine($"response_body_unchanged={responseUnchanged}");
    Console.WriteLine($"origin_request_count={originRequestCount} (expected 1)");
    Console.WriteLine("expected_admiral_exp=234567;expected_ship_total_exp=3210000");
    Console.WriteLine("After PASS, confirm one new CSV row per file beside the app executable.");
    var pass = requestUnchanged && responseUnchanged && originRequestCount == 1;
    Console.WriteLine($"PASS={pass}");
    Environment.ExitCode = pass ? 0 : 1;
}
catch (Exception ex)
{
    Console.WriteLine($"TEST ERROR={ex.GetType().Name}: {ex.Message}");
    Environment.ExitCode = 1;
}
finally
{
    origin.Stop();
}

