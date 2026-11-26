using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

static void Usage()
{
    Console.WriteLine("Usage: Phase6ProxyEngine [--listen-port 8877] [--upstream-port PORT] [--ca-pfx FILE.pfx] [--export-ca FILE.cer]");
    Console.WriteLine("Listens on 127.0.0.1 only. Never changes Windows certificate stores or system proxy settings.");
}

static int? ReadPort(string[] args, string option)
{
    var index = Array.IndexOf(args, option);
    if (index < 0) return null;
    if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var port) || port is < 1 or > 65535)
        throw new ArgumentException($"Invalid value for {option}");
    return port;
}

static string? ReadValue(string[] args, string option)
{
    var index = Array.IndexOf(args, option);
    if (index < 0) return null;
    if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException($"Missing value for {option}");
    return args[index + 1];
}

static bool TryParsePortResponse(byte[] bytes, out long admiralExp, out long shipTotalExp)
{
    admiralExp = 0;
    shipTotalExp = 0;
    var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
    if (text.StartsWith("svdata=", StringComparison.Ordinal)) text = text[7..].TrimStart();
    try
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (!root.TryGetProperty("api_result", out var result) || result.GetInt32() != 1) return false;
        var data = root.GetProperty("api_data");
        admiralExp = data.GetProperty("api_basic").GetProperty("api_experience").GetInt64();
        var ships = data.GetProperty("api_ship");
        if (ships.ValueKind != JsonValueKind.Array || ships.GetArrayLength() == 0) return false;
        long total = 0;
        foreach (var ship in ships.EnumerateArray())
        {
            var exp = ship.GetProperty("api_exp");
            if (exp.ValueKind != JsonValueKind.Array || exp.GetArrayLength() == 0) return false;
            total = checked(total + exp[0].GetInt64());
        }
        shipTotalExp = total;
        return true;
    }
    catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or OverflowException)
    {
        return false;
    }
}

if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    Usage();
    return;
}

try
{
    var listenPort = ReadPort(args, "--listen-port") ?? 8877;
    var upstreamPort = ReadPort(args, "--upstream-port");
    var caPfxPath = ReadValue(args, "--ca-pfx");
    var exportCaPath = ReadValue(args, "--export-ca");
    var proxy = new ProxyServer(false, false, false);
#if DEBUG
    proxy.Logging.MinimumLevel = LogLevel.Debug;
#else
    proxy.Logging.MinimumLevel = LogLevel.None;
#endif
    proxy.EnableHttp2 = false;
    proxy.EnableTcpServerConnectionPrefetch = false;
    proxy.NetworkFailureRetryAttempts = 0;
    proxy.ViaHeaderPseudonym = string.Empty;
    proxy.CertificateManager.PfxFilePath = caPfxPath ?? Path.Combine(Path.GetTempPath(), "senkaspeed-phase6-unused-" + Guid.NewGuid().ToString("N") + ".pfx");
    proxy.CertificateManager.SaveFakeCertificates = false;
    if (!proxy.CertificateManager.CreateRootCertificate(caPfxPath is not null))
        throw new InvalidOperationException("Could not create an in-memory test proxy CA");

#if DEBUG
    var testPin = ReadValue(args, "--test-pin-upstream-sha256");
    if (testPin is not null)
    {
        var normalizedPin = testPin.Replace(":", "", StringComparison.Ordinal).ToUpperInvariant();
        if (normalizedPin.Length != 64 || normalizedPin.Any(ch => !Uri.IsHexDigit(ch)))
            throw new ArgumentException("Test pin must be a SHA-256 certificate fingerprint");
        proxy.ServerCertificateValidationCallback += (_, e) =>
        {
            var certificate = e.Certificate;
            using var certificate2 = certificate is null ? null : new X509Certificate2(certificate);
            var fingerprint = certificate2?.GetCertHashString(HashAlgorithmName.SHA256);
            var hostnameMatches = (e.SslPolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0;
            var currentlyValid = certificate2 is not null && DateTime.UtcNow >= certificate2.NotBefore.ToUniversalTime() &&
                DateTime.UtcNow <= certificate2.NotAfter.ToUniversalTime();
            var pinMatches = string.Equals(fingerprint, normalizedPin, StringComparison.OrdinalIgnoreCase);
            if (hostnameMatches && currentlyValid && pinMatches)
                e.IsValid = true;
            Console.WriteLine($"debug_upstream_pin_match={pinMatches};hostname_valid={hostnameMatches};date_valid={currentlyValid};policy_errors={e.SslPolicyErrors}");
            return Task.CompletedTask;
        };
    }
#else
    if (args.Contains("--test-pin-upstream-sha256", StringComparer.OrdinalIgnoreCase))
        throw new ArgumentException("The test-only certificate pin is unavailable in Release builds");
#endif

    var root = proxy.CertificateManager.RootCertificate ?? throw new InvalidOperationException("Proxy CA unavailable");
    if (exportCaPath is not null)
    {
        var fullPath = Path.GetFullPath(exportCaPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, root.Export(X509ContentType.Cert));
        Console.WriteLine($"public_ca_exported={fullPath}");
    }

    var eventNumber = 0;
#if DEBUG
    proxy.BeforeRequest += (_, e) =>
    {
        Console.WriteLine($"debug_request_received=True;method={e.HttpClient.Request.Method};https={e.HttpClient.Request.IsHttps};has_body={e.HttpClient.Request.HasBody}");
        return Task.CompletedTask;
    };
#endif
    proxy.BeforeResponse += async (_, e) =>
    {
        if (e.HttpClient.Request.RequestUri?.AbsolutePath != "/kcsapi/api_port/port" ||
            e.HttpClient.Response.StatusCode != 200)
            return;
        var analysisCopy = await e.GetResponseBody();
        if (!TryParsePortResponse(analysisCopy, out var admiralExp, out var shipTotalExp)) return;
        var output = JsonSerializer.Serialize(new
        {
            type = "measurement",
            sequence = Interlocked.Increment(ref eventNumber),
            timestamp_utc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            admiral_exp = admiralExp,
            ship_total_exp = shipTotalExp,
        });
        Console.WriteLine(output);
        Console.Out.Flush();
    };
    if (upstreamPort.HasValue)
    {
        var upstream = new ExternalProxy("127.0.0.1", upstreamPort.Value);
        proxy.UpStreamHttpProxy = upstream;
        proxy.UpStreamHttpsProxy = upstream;
        proxy.ForwardToUpstreamGateway = true;
    }

    proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, listenPort, true));
    proxy.Start(false);
    Console.WriteLine($"listening=127.0.0.1:{listenPort}");
    Console.WriteLine(upstreamPort.HasValue ? $"upstream=127.0.0.1:{upstreamPort}" : "upstream=direct");
    Console.WriteLine($"proxy_ca_thumbprint={root.Thumbprint}");
#if DEBUG
    Console.WriteLine(testPin is null ? "test_upstream_pin=not_configured" : "test_upstream_pin=exact_certificate_and_hostname_checked");
#endif
    Console.WriteLine("certificate_store_changed=False; system_proxy_changed=False; raw_requests_or_responses_saved=False");
    Console.WriteLine("Press Ctrl+C to stop.");

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    var stdinStop = Console.In.ReadLineAsync();
    try { await Task.WhenAny(Task.Delay(Timeout.Infinite, stop.Token), stdinStop); }
    catch (OperationCanceledException) { }
    proxy.Stop();
    proxy.Dispose();
}
catch (Exception ex)
{
    Console.Error.WriteLine("startup_error=" + ex.GetType().Name + ": " + ex.Message);
    Environment.ExitCode = 1;
}

