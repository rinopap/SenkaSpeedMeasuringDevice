using System.Security.Cryptography;
using System.Text;
using SenkaSpeedMeasuringDevice.Capture;

var port = int.Parse(args[0]);
var pageUrl = args[1];
var apiHost = args[2];
var received = new TaskCompletionSource<SenkaSpeedMeasuringDevice.Core.ApiResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
await using var source = new CdpCaptureSource(new CdpCaptureOptions(port, pageUrl,
    new HashSet<string>([apiHost], StringComparer.OrdinalIgnoreCase)));
source.StatusChanged += (_, status) => Console.WriteLine(status);
source.ResponseReceived += (_, response) => received.TrySetResult(response);
await source.StartAsync(CancellationToken.None);
var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(45));
var hash = Convert.ToHexString(SHA256.HashData(result.ResponseBody.Span));
Console.WriteLine($"Captured /kcsapi/api_port/port: bytes={result.ResponseBody.Length}, sha256={hash}");
var expected = "419A55CD47C2F8D630C7AFD9C6E0E517672DD595AC44CBD6242346675F0D10AD";
if (!string.Equals(hash, expected, StringComparison.Ordinal)) return 2;
Console.WriteLine("PASS: response body hash equals the local test server payload.");
return 0;

