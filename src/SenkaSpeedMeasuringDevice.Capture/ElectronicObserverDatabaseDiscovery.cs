using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SenkaSpeedMeasuringDevice.Capture;

/// <summary>Finds only likely portable ElectronicObserver install locations, then validates the DB read-only.</summary>
public static class ElectronicObserverDatabaseDiscovery
{
    private const string DatabaseRelativePath = "Record/ElectronicObserver.sqlite";

    public static IReadOnlyList<string> FindValidDatabases(string? configuredPath = null)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(configuredPath)) AddCandidate(candidates, configuredPath);

        foreach (var process in Process.GetProcessesByName("ElectronicObserver"))
        {
            using (process)
            {
                try
                {
                    var executablePath = process.MainModule?.FileName;
                    if (executablePath is not null)
                    {
                        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
                        if (directory is not null) AddCandidate(candidates, Path.Combine(directory, DatabaseRelativePath));
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    // An inaccessible process is not a database candidate.
                }
            }
        }

        foreach (var root in GetNarrowSearchRoots())
        {
            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(root, "ElectronicObserver*", SearchOption.TopDirectoryOnly).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { continue; }
            foreach (var directory in directories) AddCandidate(candidates, Path.Combine(directory, DatabaseRelativePath));
        }

        return candidates.Where(IsValidDatabase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> GetNarrowSearchRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        if (!string.IsNullOrWhiteSpace(systemRoot)) roots.Add(systemRoot);
        foreach (var value in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } profile
                         ? Path.Combine(profile, "Downloads") : string.Empty
                 })
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value)) roots.Add(value);
        return roots;
    }

    private static void AddCandidate(HashSet<string> candidates, string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath)) candidates.Add(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException) { }
    }

    private static bool IsValidDatabase(string path)
    {
        try
        {
            if (!IsLocalFile(path)) return false;
            new SqliteApiFileReader(path, defaultTimeoutSeconds: 0).ValidateDatabase();
            return true;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static bool IsLocalFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root) || root.StartsWith("\\\\", StringComparison.Ordinal)) return false;
            var drive = new DriveInfo(root);
            if (drive.DriveType is DriveType.Network or DriveType.NoRootDirectory) return false;
            var current = fullPath;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool AreSameFile(string firstPath, string secondPath)
    {
        try
        {
            var first = Path.GetFullPath(firstPath);
            var second = Path.GetFullPath(secondPath);
            if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;
            if (!OperatingSystem.IsWindows()) return false;
            var firstId = TryGetFileId(first);
            var secondId = TryGetFileId(second);
            return firstId is not null && string.Equals(firstId, secondId, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? TryGetFileId(string path)
    {
        using var handle = CreateFile(path, 0x80, 0x1 | 0x2 | 0x4, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return null;
        return $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
}
