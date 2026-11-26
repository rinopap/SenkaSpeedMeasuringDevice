using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

const string responseText = "svdata={\"api_result\":1,\"api_data\":{\"api_basic\":{\"api_experience\":123456},\"api_ship\":[{\"api_exp\":[2000000,0,100]}]}}";
var responseBytes = Encoding.UTF8.GetBytes(responseText);
var requestBody = Encoding.ASCII.GetBytes("local_probe=1&keep_exact=true");

using var origin = new TcpListener(IPAddress.Loopback, 0);
origin.Start();
var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
var originTask = Task.Run(async () =>
{
    using var client = await origin.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(15));
    using var stream = client.GetStream();
    using var buffer = new MemoryStream();
    var tail = new byte[4];
    var headerEnd = -1;
    while (headerEnd < 0)
    {
        var one = new byte[1];
        if (await stream.ReadAsync(one) == 0) throw new Exception("Origin received a truncated request");
        buffer.WriteByte(one[0]);
        for (var i = 0; i < 3; i++) tail[i] = tail[i + 1];
        tail[3] = one[0];
        if (tail.SequenceEqual(new byte[] { 13, 10, 13, 10 })) headerEnd = checked((int)buffer.Length);
    }
    var headerBytes = buffer.ToArray();
    var headers = Encoding.ASCII.GetString(headerBytes, 0, headerEnd);
    var firstLine = headers.Split("\r\n", StringSplitOptions.None)[0];
    var contentLength = 0;
    foreach (var line in headers.Split("\r\n"))
        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            contentLength = int.Parse(line.Split(':', 2)[1].Trim());
    while (buffer.Length - headerEnd < contentLength)
    {
        var chunk = new byte[Math.Min(4096, contentLength - checked((int)(buffer.Length - headerEnd)))];
        var read = await stream.ReadAsync(chunk);
        if (read == 0) throw new Exception("Origin received a truncated request body");
        buffer.Write(chunk, 0, read);
    }
    var all = buffer.ToArray();
    var actualBody = all.AsSpan(headerEnd, contentLength).ToArray();
    var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {responseBytes.Length}\r\nConnection: close\r\n\r\n").Concat(responseBytes).ToArray();
    await stream.WriteAsync(response);
    await stream.FlushAsync();
    return (firstLine, headers, actualBody);
});

using var portProbe = new TcpListener(IPAddress.Loopback, 0);
portProbe.Start();
var proxyPort = ((IPEndPoint)portProbe.LocalEndpoint).Port;
portProbe.Stop();
var engineDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "phase6-proxy-engine", "bin", "Release", "net10.0", "Phase6ProxyEngine.dll"));
if (!File.Exists(engineDll)) throw new FileNotFoundException("Build Phase6ProxyEngine first", engineDll);

var start = new ProcessStartInfo { FileName = Environment.ProcessPath ?? "dotnet", UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
start.ArgumentList.Add(engineDll);
start.ArgumentList.Add("--listen-port");
start.ArgumentList.Add(proxyPort.ToString());
using var engine = Process.Start(start) ?? throw new Exception("Could not start the local capture engine");
var output = new List<string>();
var ready = false;
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (!ready)
    {
        var line = await engine.StandardOutput.ReadLineAsync(timeout.Token);
        if (line is null) throw new Exception("Capture engine exited before listen: " + await engine.StandardError.ReadToEndAsync());
        output.Add(line);
        ready = line == $"listening=127.0.0.1:{proxyPort}";
    }

    var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"), UseProxy = true };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{originPort}/kcsapi/api_port/port?check=phase6")
    { Content = new ByteArrayContent(requestBody) };
    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
    using var result = await client.SendAsync(request);
    var returnedBody = await result.Content.ReadAsByteArrayAsync();
    var observed = await originTask;

    using var eventTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    string? eventLine = null;
    while (eventLine is null)
    {
        var line = await engine.StandardOutput.ReadLineAsync(eventTimeout.Token);
        if (line is null) break;
        output.Add(line);
        if (line.StartsWith("{\"type\":\"measurement\"", StringComparison.Ordinal)) eventLine = line;
    }

    var requestMatches = observed.firstLine == "POST /kcsapi/api_port/port?check=phase6 HTTP/1.1" &&
        observed.headers.Contains($"Host: 127.0.0.1:{originPort}", StringComparison.OrdinalIgnoreCase) &&
        observed.actualBody.SequenceEqual(requestBody);
    var responseMatches = result.StatusCode == HttpStatusCode.OK && returnedBody.SequenceEqual(responseBytes);
    var metricsMatch = false;
    var noRawBodyOutput = output.All(line => !line.Contains(responseText, StringComparison.Ordinal) && !line.Contains(Encoding.ASCII.GetString(requestBody), StringComparison.Ordinal));
    if (eventLine is not null)
    {
        using var json = JsonDocument.Parse(eventLine);
        var root = json.RootElement;
        metricsMatch = root.GetProperty("admiral_exp").GetInt64() == 123456 && root.GetProperty("ship_total_exp").GetInt64() == 2000000;
    }
    Console.WriteLine($"request_method_target_body_unchanged={requestMatches}");
    Console.WriteLine($"response_body_unchanged={responseMatches}");
    Console.WriteLine($"metrics_only_observation={metricsMatch}; raw_payload_not_logged={noRawBodyOutput}");
    Console.WriteLine("origin_request_count=1 (expected 1)");
    var pass = ready && requestMatches && responseMatches && metricsMatch && noRawBodyOutput;
    Console.WriteLine($"PASS={pass}");
    Environment.ExitCode = pass ? 0 : 1;
}
catch (Exception ex)
{
    Console.WriteLine("TEST ERROR: " + ex.Message);
    Environment.ExitCode = 1;
}
finally
{
    if (!engine.HasExited) { engine.Kill(entireProcessTree: true); await engine.WaitForExitAsync(); }
    origin.Stop();
}

