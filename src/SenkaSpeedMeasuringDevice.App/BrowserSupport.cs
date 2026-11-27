using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.App;

internal sealed record BrowserInstallation(string Id, string Name, string? ExecutablePath, string Version, bool CanLaunch)
{
    public override string ToString() => $"{Name} ({Version})";
}

internal static class BrowserCatalog
{
    private sealed record Candidate(string Id, string Name, string Publisher, string RelativePath);

    private static readonly Candidate[] Supported =
    [
        new("edge", "Microsoft Edge", "Microsoft Corporation", @"Microsoft\Edge\Application\msedge.exe"),
        new("chrome", "Google Chrome", "Google LLC", @"Google\Chrome\Application\chrome.exe")
    ];

    private static readonly Candidate[] RecognizedButUnverified =
    [
        new("vivaldi", "Vivaldi", "Vivaldi Technologies AS", @"Vivaldi\Application\vivaldi.exe"),
        new("brave", "Brave", "Brave Software, Inc.", @"BraveSoftware\Brave-Browser\Application\brave.exe"),
        new("opera", "Opera", "Opera Norway AS", @"Programs\Opera\opera.exe")
    ];

    public static IReadOnlyList<BrowserInstallation> FindSupportedInstalled()
    {
        var result = new List<BrowserInstallation>();
        foreach (var candidate in Supported)
        {
            var path = FindExecutable(candidate);
            if (path is null || !HasExpectedSignature(path, candidate.Publisher)) continue;
            var version = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "バージョン不明";
            result.Add(new BrowserInstallation(candidate.Id, candidate.Name, path, version, CanLaunch: true));
        }
        var observerProcesses = Process.GetProcessesByName("ElectronicObserver");
        try
        {
            if (observerProcesses.Length > 0)
                result.Add(new BrowserInstallation("74en", "74EN内蔵WebView2（起動中）", null, "起動中", CanLaunch: false));
        }
        finally
        {
            foreach (var process in observerProcesses) process.Dispose();
        }
        return result;
    }

    public static string? GetDefaultBrowserId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var programId = key?.GetValue("ProgId") as string;
            if (programId?.StartsWith("MSEdge", StringComparison.OrdinalIgnoreCase) == true) return "edge";
            if (programId?.StartsWith("Chrome", StringComparison.OrdinalIgnoreCase) == true) return "chrome";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }

    public static string DescribeUnverifiedInstalled()
    {
        var names = RecognizedButUnverified
            .Where(candidate => FindExecutable(candidate) is not null)
            .Select(candidate => candidate.Name)
            .ToArray();
        return names.Length == 0
            ? "Chrome / Edge を正式対応対象として検出します。その他のブラウザは未検証のため選択肢に表示しません。"
            : $"未検証のため選択できないブラウザ: {string.Join("、", names)}。Chrome / Edge を使用してください。";
    }

    public static bool HasExpectedSignature(string path, string expectedPublisher)
    {
        try
        {
            if (!HasTrustedAuthenticodeSignature(path)) return false;
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var publisher = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return publisher.Contains(expectedPublisher, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasTrustedAuthenticodeSignature(string path)
    {
        var filePath = Marshal.StringToCoTaskMemUni(path);
        var fileInfo = new WinTrustFileInfo
        {
            Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = filePath
        };
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                UnionChoice = 1,
                FileInfo = fileInfoPointer,
                ProviderFlags = 0x00001000
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
            Marshal.FreeCoTaskMem(filePath);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr windowHandle, ref Guid actionId, ref WinTrustData trustData);

    private static string? FindExecutable(Candidate candidate)
    {
        var paths = new List<string>();
        var appPath = ReadAppPath(candidate.Id switch
        {
            "edge" => "msedge.exe",
            "chrome" => "chrome.exe",
            "vivaldi" => "vivaldi.exe",
            "brave" => "brave.exe",
            _ => "opera.exe"
        });
        if (appPath is not null) paths.Add(appPath);

        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        }.Where(root => !string.IsNullOrWhiteSpace(root)))
            paths.Add(Path.Combine(root, candidate.RelativePath));

        return paths.FirstOrDefault(File.Exists);
    }

    private static string? ReadAppPath(string executable)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(hive, view)
                    .OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executable}");
                if (key?.GetValue(null) is string value && File.Exists(value)) return value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return null;
    }
}

internal static class BrowserLaunchHelper
{
    public static Task<(bool Success, int? Port, string Message)> PrepareLoginAsync(
        BrowserInstallation browser, CancellationToken cancellationToken = default) =>
        LaunchAsync(browser, enableCdp: false, cancellationToken);

    public static async Task<(bool Success, int? Port, string Message)> LaunchAsync(
        BrowserInstallation browser, CancellationToken cancellationToken = default) =>
        await LaunchAsync(browser, enableCdp: true, cancellationToken);

    private static async Task<(bool Success, int? Port, string Message)> LaunchAsync(
        BrowserInstallation browser, bool enableCdp, CancellationToken cancellationToken)
    {
        if (!browser.CanLaunch || browser.ExecutablePath is null)
            return (false, null, "74ENはこの補助機能から起動・終了しません。74ENを起動した状態で詳細情報のCDPポートを入力してください。");

        var expectedPublisher = browser.Id == "edge" ? "Microsoft Corporation" : "Google LLC";
        if (!BrowserCatalog.HasExpectedSignature(browser.ExecutablePath, expectedPublisher))
            return (false, null, "ブラウザの署名を確認できませんでした。起動を中止しました。");

        var profile = GetProfileDirectory(browser.Id);
        if (HasProfileLock(profile))
            return (false, null, "専用ブラウザプロファイルが使用中です。開いている専用ブラウザを閉じてから再試行してください。");

        try { Directory.CreateDirectory(profile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (false, null, "専用プロファイルを作成できませんでした。ユーザーフォルダーの空き容量とアクセス権を確認してください。");
        }
        var activePortFile = Path.Combine(profile, "DevToolsActivePort");
        if (enableCdp)
        {
            try { if (File.Exists(activePortFile)) File.Delete(activePortFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (false, null, "専用ブラウザの接続情報を準備できませんでした。");
            }
        }

        var start = new ProcessStartInfo(browser.ExecutablePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(browser.ExecutablePath)! };
        foreach (var argument in BrowserLaunchPlan.CreateArguments(enableCdp, profile))
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return (false, null, "ブラウザを起動できませんでした。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return (false, null, "ブラウザを起動できませんでした。");
        }

        if (!enableCdp)
            return (true, null, "専用プロファイルを通常モードで開きました。ブラウザ上でログイン操作を行ってください。計測機はログイン状態を確認しません。ログイン後はブラウザを利用者自身で閉じ、設定から「計測開始」を選んでください。計測機はブラウザを終了しません。");

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(activePortFile, cancellationToken);
                if (lines.Length >= 2 && int.TryParse(lines[0], out var port) && port is >= 1 and <= 65535 &&
                    lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
                    {
                        Timeout = TimeSpan.FromSeconds(2)
                    };
                    using var versionResponse = await http.GetAsync($"http://127.0.0.1:{port}/json/version", cancellationToken);
                    if (!versionResponse.IsSuccessStatusCode) continue;
                    if (versionResponse.Content.Headers.ContentLength is > 16 * 1024) continue;
                    await using var versionStream = await versionResponse.Content.ReadAsStreamAsync(cancellationToken);
                    var versionBytes = new byte[16 * 1024 + 1];
                    var versionLength = await versionStream.ReadAsync(versionBytes, cancellationToken);
                    if (versionLength > 16 * 1024) continue;
                    var versionBody = System.Text.Encoding.UTF8.GetString(versionBytes, 0, versionLength);
                    if (!versionBody.Contains(browser.Id == "edge" ? "Edg/" : "Chrome/", StringComparison.Ordinal)) continue;
                    return (true, port, "計測用ブラウザをCDP付きで起動しました。計測は利用者が開始した後、対象APIを受信してから始まります。CDP接続を切ってもブラウザ終了まではデバッグ待受が残ります。計測機を閉じてもブラウザは終了しません。");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or SocketException or
                                       HttpRequestException or OperationCanceledException)
            {
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) break;
            }
            await Task.Delay(250, cancellationToken);
        }
        return (false, null, "ブラウザは起動しましたが、デバッグ接続先を確認できませんでした。ブラウザはそのまま起動しています。設定ポートを手動で確認してください。");
    }

    public static string GetProfileDirectory(string browserId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SenkaSpeedMeasuringDevice", "BrowserProfiles", browserId);

    private static bool HasProfileLock(string profile) =>
        File.Exists(Path.Combine(profile, "SingletonLock")) ||
        File.Exists(Path.Combine(profile, "SingletonCookie")) ||
        File.Exists(Path.Combine(profile, "SingletonSocket"));
}
