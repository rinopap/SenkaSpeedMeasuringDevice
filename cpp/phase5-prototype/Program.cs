using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

[assembly: SupportedOSPlatform("windows")]

var expectedResponse = Encoding.UTF8.GetBytes("svdata={\"api_result\":1,\"api_data\":{\"api_basic\":{\"api_experience\":123456},\"api_ship\":[]}}");

static (X509Certificate2 Certificate, string KeyName) MakeOriginCertificate()
{
    var keyName = "SenkaPhase5Test-" + Guid.NewGuid().ToString("N");
    var key = new RSACng(CngKey.Create(CngAlgorithm.Rsa, keyName, new CngKeyCreationParameters
    {
        ExportPolicy = CngExportPolicies.None,
        KeyUsage = CngKeyUsages.Signing | CngKeyUsages.Decryption,
    }));
    var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
    var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); req.CertificateExtensions.Add(san.Build());
    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
    return (req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1)), keyName);
}

static bool ChainsTo(X509Certificate certificate, X509Certificate2 root)
{
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(root);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    return chain.Build(new X509Certificate2(certificate));
}

static bool HasExpectedPortData(byte[] body)
{
    var text = Encoding.UTF8.GetString(body);
    if (text.StartsWith("svdata=", StringComparison.Ordinal)) text = text[7..];
    using var json = JsonDocument.Parse(text);
    var data = json.RootElement.GetProperty("api_data");
    return json.RootElement.GetProperty("api_result").GetInt32() == 1 &&
           data.GetProperty("api_basic").GetProperty("api_experience").GetInt64() == 123456 &&
           data.GetProperty("api_ship").GetArrayLength() == 0;
}

static void SetCertificatePolicy(ProxyServer proxy, X509Certificate2 origin, X509Certificate2? upstreamProxyRoot)
{
    proxy.ServerCertificateValidationCallback += (_, e) =>
    {
        // The local HTTPS origin is self-signed for this test only. Pin that exact fixture cert.
        if (e.Certificate.GetCertHashString() == origin.GetCertHashString()) e.IsValid = true;
        // In B/C, explicitly trust only the test peer-proxy root. Name checks remain enabled.
        else if ((e.SslPolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 &&
                 upstreamProxyRoot is not null && ChainsTo(e.Certificate, upstreamProxyRoot)) e.IsValid = true;
        return Task.CompletedTask;
    };
}

static ProxyServer StartProxy(X509Certificate2 origin, X509Certificate2? upstreamProxyRoot, int? upstreamPort, Action<byte[]> responseSink)
{
    var proxy = new ProxyServer(false, false, false); // never trust a CA in a Windows certificate store
    proxy.CertificateManager.PfxFilePath = Path.Combine(Path.GetTempPath(), "senkaspeed-phase5-unused-" + Guid.NewGuid().ToString("N") + ".pfx");
    proxy.CertificateManager.SaveFakeCertificates = false;
    if (!proxy.CertificateManager.CreateRootCertificate(false)) throw new Exception("test-only proxy CA creation failed");
    SetCertificatePolicy(proxy, origin, upstreamProxyRoot);
    proxy.ExceptionFunc = ex => Console.WriteLine("PROXY ERROR: " + ex.GetType().Name + ": " + ex.Message);
    proxy.BeforeResponse += async (_, e) =>
    {
        if (e.HttpClient.Request.RequestUri?.AbsolutePath == "/kcsapi/api_port/port" && e.HttpClient.Response.StatusCode == 200)
            responseSink(await e.GetResponseBody());
    };
    if (upstreamPort is int port)
    {
        proxy.UpStreamHttpProxy = new ExternalProxy("127.0.0.1", port);
        proxy.UpStreamHttpsProxy = new ExternalProxy("127.0.0.1", port);
        proxy.ForwardToUpstreamGateway = true;
    }
    proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 0, true));
    proxy.Start(false); // do not read/change system proxy configuration
    return proxy;
}

static int PortOf(ProxyServer proxy) => proxy.ProxyEndPoints[0].Port;

static async Task SendThroughProxy(int port, X509Certificate2 trustedProxyRoot, int originPort, string mode, byte[] reqBody, byte[] expectedResponse)
{
    var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true };
    handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
    {
        if (cert is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
        return ChainsTo(cert, trustedProxyRoot);
    };
    using var http = new HttpClient(handler);
    using var request = new HttpRequestMessage(HttpMethod.Post,
        $"https://127.0.0.1:{originPort}/kcsapi/api_port/port?probe=phase5&mode={mode}&x=7")
    { Content = new ByteArrayContent(reqBody) };
    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
    using var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsByteArrayAsync();
    if (response.StatusCode != HttpStatusCode.OK) throw new Exception($"{mode}: origin response status {response.StatusCode}");
    if (!body.SequenceEqual(expectedResponse)) throw new Exception($"{mode}: client response body differs");
}

var requestBody = Encoding.ASCII.GetBytes("probe=phase5&api_test=1");
var responseSeen = new ConcurrentDictionary<string, byte[]>();
var requestsSeen = new ConcurrentDictionary<string, (string Method, string Target, string Host, byte[] Body)>();
var requestCount = 0;
var (originCert, originKeyName) = MakeOriginCertificate();
using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
var originPort = ((IPEndPoint)listener.LocalEndpoint).Port;
var originTask = Task.Run(async () =>
{
    var handlers = new List<Task>();
    for (var n = 0; n < 3; n++)
    {
        var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(30));
        handlers.Add(Task.Run(async () =>
        {
            using (client)
            using (var ssl = new SslStream(client.GetStream(), false))
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = originCert, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
                using var ms = new MemoryStream(); var buf = new byte[4096]; var headerEnd = -1;
                while (headerEnd < 0)
                {
                    var read = await ssl.ReadAsync(buf); if (read == 0) throw new Exception("request headers ended early");
                    ms.Write(buf, 0, read); var bytes = ms.ToArray();
                    for (var i = Math.Max(0, bytes.Length - read - 3); i <= bytes.Length - 4; i++)
                        if (bytes[i] == 13 && bytes[i+1] == 10 && bytes[i+2] == 13 && bytes[i+3] == 10) { headerEnd = i + 4; break; }
                }
                var received = ms.ToArray(); var headers = Encoding.ASCII.GetString(received, 0, headerEnd);
                var first = headers.Split("\r\n", StringSplitOptions.None)[0].Split(' ');
                var length = 0;
                foreach (var line in headers.Split("\r\n")) if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Split(':', 2)[1].Trim());
                while (ms.Length - headerEnd < length) { var read = await ssl.ReadAsync(buf); if (read == 0) throw new Exception("request body ended early"); ms.Write(buf, 0, read); }
                received = ms.ToArray(); var hostLine = headers.Split("\r\n").FirstOrDefault(x => x.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) ?? "";
                var mode = first[1].Split("mode=").Last().Split('&')[0];
                requestsSeen[mode] = (first[0], first[1], hostLine, received.AsSpan(headerEnd, length).ToArray());
                Interlocked.Increment(ref requestCount);
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {expectedResponse.Length}\r\nConnection: close\r\n\r\n").Concat(expectedResponse).ToArray();
                await ssl.WriteAsync(response); await ssl.FlushAsync();
            }
        }));
    }
    await Task.WhenAll(handlers);
});

try
{
    // A: browser/client -> capture proxy -> local TLS origin
        using (var appA = StartProxy(originCert, null, null, b => responseSeen["A"] = b))
        await SendThroughProxy(PortOf(appA), appA.CertificateManager.RootCertificate!, originPort, "A", requestBody, expectedResponse);

    // B: browser/client -> 74EN-equivalent proxy -> capture proxy -> local TLS origin
    using (var appB = StartProxy(originCert, null, null, b => responseSeen["B"] = b))
    using (var eoB = StartProxy(originCert, appB.CertificateManager.RootCertificate, PortOf(appB), _ => { }))
        await SendThroughProxy(PortOf(eoB), eoB.CertificateManager.RootCertificate!, originPort, "B", requestBody, expectedResponse);

    // C: browser/client -> capture proxy -> 74EN-equivalent proxy -> local TLS origin
    using (var eoC = StartProxy(originCert, null, null, _ => { }))
    using (var appC = StartProxy(originCert, eoC.CertificateManager.RootCertificate, PortOf(eoC), b => responseSeen["C"] = b))
        await SendThroughProxy(PortOf(appC), appC.CertificateManager.RootCertificate!, originPort, "C", requestBody, expectedResponse);

    await originTask.WaitAsync(TimeSpan.FromSeconds(30));
    var allPass = true;
    foreach (var mode in new[] { "A", "B", "C" })
    {
        var observed = requestsSeen[mode];
        var expectedTarget = $"/kcsapi/api_port/port?probe=phase5&mode={mode}&x=7";
        var requestOk = observed.Method == "POST" && observed.Target == expectedTarget &&
                        observed.Host == $"Host: 127.0.0.1:{originPort}" && observed.Body.SequenceEqual(requestBody);
        var responseOk = responseSeen.TryGetValue(mode, out var body) && body.SequenceEqual(expectedResponse);
        var parseOk = responseOk && HasExpectedPortData(body!);
        var pass = requestOk && responseOk;
        allPass &= pass && parseOk;
        Console.WriteLine($"{mode}: method_destination_query_body={(requestOk ? "PASS" : "FAIL")}, client_and_analysis_body={(responseOk ? "PASS" : "FAIL")}, port_response_parse={(parseOk ? "PASS" : "FAIL")}");
    }
    Console.WriteLine($"origin_request_count={requestCount} (expected 3; one per explicit local test request)");
    allPass &= requestCount == 3;
    Console.WriteLine($"tls_validation=enabled; test root CAs trusted only through in-process custom chains; no Windows certificate store or system proxy changed");
    Console.WriteLine($"PASS={allPass}");
    Environment.ExitCode = allPass ? 0 : 1;
}
catch (Exception ex)
{
    Console.WriteLine("TEST ERROR: " + ex);
    Environment.ExitCode = 1;
}
finally
{
    listener.Stop(); originCert.Dispose();
    try { CngKey.Open(originKeyName).Delete(); } catch { }
}


