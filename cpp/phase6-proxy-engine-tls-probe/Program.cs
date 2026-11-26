using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

static (X509Certificate2 Certificate, string KeyName) MakeOriginCertificate()
{
    var keyName = "SenkaPhase6Origin-" + Guid.NewGuid().ToString("N");
    var key = new RSACng(CngKey.Create(CngAlgorithm.Rsa, keyName, new CngKeyCreationParameters
    {
        ExportPolicy = CngExportPolicies.None,
        KeyUsage = CngKeyUsages.Signing | CngKeyUsages.Decryption,
    }));
    var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
    var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
    return (request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1)), keyName);
}

static bool ChainsTo(X509Certificate certificate, X509Certificate2 root)
{
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(root);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    return chain.Build(new X509Certificate2(certificate));
}

var responseBytes = Encoding.UTF8.GetBytes("svdata={\"api_result\":1,\"api_data\":{\"api_basic\":{\"api_experience\":123456},\"api_ship\":[{\"api_exp\":[2000000,0,100]}]}}" );
var requestBody = Encoding.ASCII.GetBytes("local_tls_probe=1&keep_exact=true");
var (originCertificate, originKeyName) = MakeOriginCertificate();
using var origin = new TcpListener(IPAddress.Loopback, 0);
origin.Start();
var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
var originRequestCount = 0;
var originTask = Task.Run(async () =>
{
    using var tcp = await origin.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(20));
    using var tls = new SslStream(tcp.GetStream(), false);
    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
    { ServerCertificate = originCertificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
    Console.WriteLine("origin_tls_handshake=completed");
    using var buffer = new MemoryStream();
    using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var tail = new byte[4];
    var headerEnd = -1;
    while (headerEnd < 0)
    {
        var one = new byte[1];
        int count;
        try { count = await tls.ReadAsync(one, readTimeout.Token); }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"origin_initial_data_bytes={buffer.Length};prefix_hex={Convert.ToHexString(buffer.ToArray().Take(32).ToArray())}");
            throw;
        }
        if (count == 0) throw new Exception("TLS origin request ended early");
        buffer.WriteByte(one[0]);
        for (var i = 0; i < 3; i++) tail[i] = tail[i + 1];
        tail[3] = one[0];
        if (tail.SequenceEqual(new byte[] { 13, 10, 13, 10 })) headerEnd = checked((int)buffer.Length);
    }
    var headers = Encoding.ASCII.GetString(buffer.ToArray(), 0, headerEnd);
    var firstLine = headers.Split("\r\n", StringSplitOptions.None)[0];
    Console.WriteLine("origin_received_request_line=" + firstLine);
    var contentLength = 0;
    foreach (var line in headers.Split("\r\n"))
        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            contentLength = int.Parse(line.Split(':', 2)[1].Trim());
    while (buffer.Length - headerEnd < contentLength)
    {
        var chunk = new byte[Math.Min(4096, contentLength - checked((int)(buffer.Length - headerEnd)))];
        var read = await tls.ReadAsync(chunk);
        if (read == 0) throw new Exception("TLS origin request body ended early");
        buffer.Write(chunk, 0, read);
    }
    var all = buffer.ToArray();
    var actualBody = all.AsSpan(headerEnd, contentLength).ToArray();
    Interlocked.Increment(ref originRequestCount);
    var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {responseBytes.Length}\r\nConnection: close\r\n\r\n").Concat(responseBytes).ToArray();
    await tls.WriteAsync(response);
    await tls.FlushAsync();
    return (firstLine, headers, actualBody);
});

using var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
var proxyPort = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
var certPath = Path.Combine(Path.GetTempPath(), "senka-phase6-proxy-root-" + Guid.NewGuid().ToString("N") + ".cer");
var caPfxPath = Path.Combine(Path.GetTempPath(), "senka-phase6-proxy-root-" + Guid.NewGuid().ToString("N") + ".pfx");
var engineDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "phase6-proxy-engine", "bin", "Debug", "net10.0", "Phase6ProxyEngine.dll"));
if (!File.Exists(engineDll)) throw new FileNotFoundException("Build Phase6ProxyEngine in Debug first", engineDll);
var start = new ProcessStartInfo { FileName = Environment.ProcessPath ?? "dotnet", UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
start.ArgumentList.Add(engineDll);
start.ArgumentList.Add("--listen-port"); start.ArgumentList.Add(proxyPort.ToString());
start.ArgumentList.Add("--ca-pfx"); start.ArgumentList.Add(caPfxPath);
start.ArgumentList.Add("--export-ca"); start.ArgumentList.Add(certPath);
start.ArgumentList.Add("--test-pin-upstream-sha256"); start.ArgumentList.Add(originCertificate.GetCertHashString(HashAlgorithmName.SHA256));
using var engine = Process.Start(start) ?? throw new Exception("Could not start the local capture engine");
var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
var readySource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
engine.OutputDataReceived += (_, e) =>
{
    if (e.Data is null) return;
    output.Enqueue(e.Data);
    if (e.Data == $"listening=127.0.0.1:{proxyPort}") readySource.TrySetResult();
};
engine.BeginOutputReadLine();
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await readySource.Task.WaitAsync(timeout.Token);

    using var proxyRoot = X509CertificateLoader.LoadCertificateFromFile(certPath);
    using var persistedRoot = X509CertificateLoader.LoadPkcs12FromFile(caPfxPath, "", X509KeyStorageFlags.Exportable);
    var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"), UseProxy = true };
    handler.SslProtocols = SslProtocols.Tls12;
    handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
    {
        var valid = certificate is not null && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 && ChainsTo(certificate, proxyRoot);
        Console.WriteLine($"client_proxy_certificate_validation={valid};ssl_policy_errors={errors}");
        return valid;
    };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    using var request = new HttpRequestMessage(HttpMethod.Post, $"https://localhost:{originPort}/kcsapi/api_port/port?check=phase6tls")
    { Content = new ByteArrayContent(requestBody) };
    request.Version = HttpVersion.Version11;
    request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
    request.Headers.ExpectContinue = false;
    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
    using var result = await client.SendAsync(request);
    var returnedBody = await result.Content.ReadAsByteArrayAsync();
    var observed = await originTask;

    using var eventTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    string? eventLine = null;
    while (eventLine is null)
    {
        eventLine = output.FirstOrDefault(line => line.StartsWith("{\"type\":\"measurement\"", StringComparison.Ordinal));
        if (eventLine is null) await Task.Delay(20, eventTimeout.Token);
    }
    var requestMatches = observed.firstLine == "POST /kcsapi/api_port/port?check=phase6tls HTTP/1.1" &&
        observed.headers.Contains($"Host: localhost:{originPort}", StringComparison.OrdinalIgnoreCase) && observed.actualBody.SequenceEqual(requestBody);
    var responseMatches = result.StatusCode == HttpStatusCode.OK && returnedBody.SequenceEqual(responseBytes);
    var caPersisted = string.Equals(proxyRoot.Thumbprint, persistedRoot.Thumbprint, StringComparison.OrdinalIgnoreCase) && persistedRoot.HasPrivateKey;
    var metricsMatch = false;
    var noRawBodyOutput = output.All(line => !line.Contains(Encoding.UTF8.GetString(responseBytes), StringComparison.Ordinal) && !line.Contains(Encoding.ASCII.GetString(requestBody), StringComparison.Ordinal));
    if (eventLine is not null)
    {
        using var json = JsonDocument.Parse(eventLine);
        metricsMatch = json.RootElement.GetProperty("admiral_exp").GetInt64() == 123456 &&
            json.RootElement.GetProperty("ship_total_exp").GetInt64() == 2000000;
    }
    Console.WriteLine($"tls_upstream_pin_and_proxy_ca_validation=PASS");
    Console.WriteLine($"proxy_ca_private_key_persisted_for_stable_identity={caPersisted}");
    Console.WriteLine($"request_method_target_body_unchanged={requestMatches}");
    Console.WriteLine($"response_body_unchanged={responseMatches}");
    Console.WriteLine($"metrics_only_observation={metricsMatch}; raw_payload_not_logged={noRawBodyOutput}");
    Console.WriteLine($"origin_request_count={originRequestCount} (expected 1)");
    var pass = requestMatches && responseMatches && metricsMatch && noRawBodyOutput && caPersisted && originRequestCount == 1;
    Console.WriteLine($"PASS={pass}");
    Environment.ExitCode = pass ? 0 : 1;
}
catch (Exception ex)
{
    Console.WriteLine("TEST ERROR: " + ex.GetType().Name + ": " + ex.Message + " inner=" + ex.InnerException?.GetType().Name + ":" + ex.InnerException?.Message);
    try { await originTask.WaitAsync(TimeSpan.FromSeconds(7)); }
    catch (Exception originError) { Console.WriteLine("origin_task_result=" + originError.GetType().Name + ":" + originError.Message); }
    foreach (var line in output) Console.WriteLine("engine_output=" + line);
    if (!engine.HasExited) { engine.Kill(entireProcessTree: true); await engine.WaitForExitAsync(); }
    try { Console.WriteLine("engine_remaining_output=" + await engine.StandardOutput.ReadToEndAsync()); } catch { }
    try { Console.WriteLine("engine_error=" + await engine.StandardError.ReadToEndAsync()); } catch { }
    Environment.ExitCode = 1;
}
finally
{
    if (!engine.HasExited) { engine.Kill(entireProcessTree: true); await engine.WaitForExitAsync(); }
    origin.Stop();
    originCertificate.Dispose();
    try { CngKey.Open(originKeyName).Delete(); } catch { }
    try { File.Delete(certPath); } catch { }
    try { File.Delete(caPfxPath); } catch { }
}

