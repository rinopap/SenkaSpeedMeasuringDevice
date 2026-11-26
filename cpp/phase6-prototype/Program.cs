using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

if (args.Length == 2 && args[0] == "--hold-port")
{
    var childProxy = new ProxyServer(false, false, false);
    childProxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, int.Parse(args[1]), false));
    childProxy.Start(false);
    Console.WriteLine("READY");
    Console.Out.Flush();
    Console.ReadLine();
    childProxy.Dispose();
    return;
}

var body = Encoding.UTF8.GetBytes("phase6-local-ok");
var receivedPaths = new List<string>();
var observedResponses = 0;

static bool IsBindCollision(Exception exception)
{
    for (Exception? current = exception; current is not null; current = current.InnerException)
    {
        if (current is SocketException socket &&
            socket.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            return true;
    }
    return false;
}

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var originPort = ((IPEndPoint)listener.LocalEndpoint).Port;
var originTask = Task.Run(async () =>
{
    for (var i = 0; i < 2; i++)
    {
        using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(20));
        using var stream = client.GetStream();
        var bytes = new List<byte>();
        var tail = new byte[4];
        while (true)
        {
            var next = new byte[1];
            var read = await stream.ReadAsync(next);
            if (read == 0) throw new Exception("Origin request ended early");
            bytes.Add(next[0]);
            for (var j = 0; j < 3; j++) tail[j] = tail[j + 1];
            tail[3] = next[0];
            if (tail.SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
        }
        var headers = Encoding.ASCII.GetString(bytes.ToArray());
        var firstLine = headers.Split("\r\n", StringSplitOptions.None)[0];
        receivedPaths.Add(firstLine);
        var payload = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n").Concat(body).ToArray();
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }
});

var reserved = new TcpListener(IPAddress.Loopback, 0);
reserved.Start();
var proxyPort = ((IPEndPoint)reserved.LocalEndpoint).Port;
var collisionObserved = false;
var collidedProxy = new ProxyServer(false, false, false);
collidedProxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, proxyPort, false));
try
{
    collidedProxy.Start(false);
}
catch (Exception ex) when (IsBindCollision(ex))
{
    collisionObserved = true;
}
finally
{
    collidedProxy.Dispose();
    reserved.Stop();
}

ProxyServer StartCapture()
{
    var proxy = new ProxyServer(false, false, false);
    proxy.ExceptionFunc = ex => Console.WriteLine("proxy_error=" + ex.GetType().Name);
    proxy.BeforeResponse += async (_, e) =>
    {
        if (e.HttpClient.Request.RequestUri?.AbsolutePath == "/phase6")
        {
            var observed = await e.GetResponseBody();
            if (observed.SequenceEqual(body)) Interlocked.Increment(ref observedResponses);
        }
    };
    proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, proxyPort, false));
    proxy.Start(false);
    return proxy;
}

async Task<bool> SendOnce()
{
    var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"), UseProxy = true };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
    using var response = await client.GetAsync($"http://127.0.0.1:{originPort}/phase6");
    var received = await response.Content.ReadAsByteArrayAsync();
    return response.StatusCode == HttpStatusCode.OK && received.SequenceEqual(body);
}

async Task<bool> ForcedProcessExitReleasesPort()
{
    var start = new ProcessStartInfo
    {
        FileName = "dotnet",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--hold-port");
    start.ArgumentList.Add(proxyPort.ToString());
    using var child = Process.Start(start) ?? throw new Exception("Could not start local proxy child process");
    var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    if (ready != "READY")
    {
        var error = await child.StandardError.ReadToEndAsync();
        throw new Exception("Child proxy did not start: " + error);
    }
    child.Kill(entireProcessTree: true);
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    using var reclaimed = new TcpListener(IPAddress.Loopback, proxyPort);
    reclaimed.Start();
    reclaimed.Stop();
    return true;
}

var firstOk = false;
var stoppedProxyUnavailable = false;
var recoveredOk = false;
var processExitReleasedPort = false;
try
{
    if (!collisionObserved) throw new Exception("Port conflict was not detected");
    using (var proxy = StartCapture()) firstOk = await SendOnce();

    try { _ = await SendOnce(); }
    catch (HttpRequestException) { stoppedProxyUnavailable = true; }
    await Task.Delay(300);

    using (var proxy = StartCapture()) recoveredOk = await SendOnce();
    await originTask.WaitAsync(TimeSpan.FromSeconds(20));
    processExitReleasedPort = await ForcedProcessExitReleasesPort();

    var exactlyOnePerExplicitSend = receivedPaths.Count == 2 && observedResponses == 2 &&
        receivedPaths.All(line => line.StartsWith("GET /phase6 HTTP/1.1", StringComparison.Ordinal));
    var pass = collisionObserved && firstOk && stoppedProxyUnavailable && recoveredOk &&
        exactlyOnePerExplicitSend && processExitReleasedPort;
    Console.WriteLine($"port_conflict_detected={collisionObserved}");
    Console.WriteLine($"first_request={firstOk}; stopped_proxy_fails_closed={stoppedProxyUnavailable}; restart_same_port={recoveredOk}");
    Console.WriteLine($"forced_process_exit_releases_port={processExitReleasedPort}");
    Console.WriteLine($"origin_request_count={receivedPaths.Count} (expected 2; one per explicit successful local request)");
    Console.WriteLine($"response_observation_count={observedResponses} (expected 2)");
    Console.WriteLine($"PASS={pass}");
    Environment.ExitCode = pass ? 0 : 1;
}
catch (Exception ex)
{
    Console.WriteLine("TEST ERROR: " + ex);
    Environment.ExitCode = 1;
}
finally
{
    listener.Stop();
}

