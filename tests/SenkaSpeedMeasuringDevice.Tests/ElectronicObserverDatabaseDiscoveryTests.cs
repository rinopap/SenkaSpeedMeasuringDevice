using System.Runtime.InteropServices;
using SenkaSpeedMeasuringDevice.Capture;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class ElectronicObserverDatabaseDiscoveryTests
{
    [Fact]
    public void AcceptsRegularLocalFileAndRecognizesItAsTheSameFile()
    {
        using var fixture = new DiscoveryFixture();
        File.WriteAllText(fixture.FirstPath, "synthetic");
        File.WriteAllText(fixture.SecondPath, "other");

        Assert.True(ElectronicObserverDatabaseDiscovery.IsLocalFile(fixture.FirstPath));
        Assert.True(ElectronicObserverDatabaseDiscovery.AreSameFile(fixture.FirstPath, fixture.FirstPath));
        Assert.False(ElectronicObserverDatabaseDiscovery.AreSameFile(fixture.FirstPath, fixture.SecondPath));
    }

    [Fact]
    public void RecognizesHardLinkAsSameFileOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new DiscoveryFixture();
        File.WriteAllText(fixture.FirstPath, "synthetic");
        Assert.True(CreateHardLink(fixture.SecondPath, fixture.FirstPath, IntPtr.Zero),
            $"CreateHardLink failed with Win32 error {Marshal.GetLastWin32Error()}.");

        Assert.True(ElectronicObserverDatabaseDiscovery.AreSameFile(fixture.FirstPath, fixture.SecondPath));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private sealed class DiscoveryFixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "SenkaDiscoveryTests", Guid.NewGuid().ToString("N"));
        public string FirstPath => Path.Combine(DirectoryPath, "first.sqlite");
        public string SecondPath => Path.Combine(DirectoryPath, "second.sqlite");

        public DiscoveryFixture() => Directory.CreateDirectory(DirectoryPath);

        public void Dispose()
        {
            try { Directory.Delete(DirectoryPath, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
