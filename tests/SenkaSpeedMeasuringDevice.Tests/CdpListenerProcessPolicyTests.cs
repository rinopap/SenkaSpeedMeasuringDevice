using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SenkaSpeedMeasuringDevice.Capture;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class CdpListenerProcessPolicyTests
{
    [Fact]
    public void ConfirmsLoopbackListenerExecutableAndRejectsOtherPath()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var process = Process.GetCurrentProcess();
        var processPath = process.MainModule?.FileName;

        Assert.False(string.IsNullOrWhiteSpace(processPath));
        Assert.True(CdpListenerProcessPolicy.IsOwnedByProcessPath(((IPEndPoint)listener.LocalEndpoint).Port, processPath!));
        Assert.False(CdpListenerProcessPolicy.IsOwnedByProcessPath(((IPEndPoint)listener.LocalEndpoint).Port,
            Path.Combine(Path.GetTempPath(), "other-browser.exe")));
    }

    [Fact]
    public void RejectsNonFullyQualifiedExpectedExecutablePath()
    {
        var options = new CdpCaptureOptions(9222, 7, "chrome.exe");
        Assert.Throws<ArgumentException>(options.Validate);
    }
}
