using SenkaSpeedMeasuringDevice.Capture;

var port = int.Parse(args[0]);
var serverId = int.Parse(args[1]);
var received = new TaskCompletionSource<SenkaSpeedMeasuringDevice.Core.ApiResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
await using var source = new CdpCaptureSource(new CdpCaptureOptions(port, serverId));
source.StatusChanged += (_, status) => Console.WriteLine(status);
source.ResponseReceived += (_, response) => received.TrySetResult(response);
await source.StartAsync(CancellationToken.None);
var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(45));
Console.WriteLine($"Captured /kcsapi/api_port/port: bytes={result.ResponseBody.Length}");
Console.WriteLine("PASS: fixed game page and selected server response observed.");
return 0;
