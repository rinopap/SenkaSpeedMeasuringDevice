using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace SenkaSpeedMeasuringDevice.Capture;

/// <summary>Checks that a loopback CDP listener belongs to the selected browser executable.</summary>
public static class CdpListenerProcessPolicy
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AddressFamilyIpv4 = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint TcpStateListen = 2;
    private const int RowSize = sizeof(uint) * 6;

    public static bool IsOwnedByProcessPath(int port, string expectedProcessPath)
    {
        if (!OperatingSystem.IsWindows() || port is < 1 or > 65535 ||
            !Path.IsPathFullyQualified(expectedProcessPath)) return false;

        try
        {
            uint size = 0;
            var result = GetExtendedTcpTable(IntPtr.Zero, ref size, order: false, AddressFamilyIpv4,
                TcpTableOwnerPidListener, reserved: 0);
            if (result != ErrorInsufficientBuffer || size < sizeof(uint)) return false;

            var table = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                result = GetExtendedTcpTable(table, ref size, order: false, AddressFamilyIpv4,
                    TcpTableOwnerPidListener, reserved: 0);
                if (result != 0) return false;

                var rowCount = Marshal.ReadInt32(table);
                if (rowCount < 0 || sizeof(uint) + (long)rowCount * RowSize > size) return false;
                for (var index = 0; index < rowCount; index++)
                {
                    var row = IntPtr.Add(table, sizeof(uint) + index * RowSize);
                    if ((uint)Marshal.ReadInt32(row) != TcpStateListen) continue;
                    var localAddress = (uint)Marshal.ReadInt32(row, sizeof(uint));
                    if (localAddress != BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes())) continue;
                    var networkPort = (uint)Marshal.ReadInt32(row, sizeof(uint) * 2);
                    var localPort = ((networkPort & 0xff) << 8) | ((networkPort >> 8) & 0xff);
                    if (localPort != (uint)port) continue;
                    var processId = Marshal.ReadInt32(row, sizeof(uint) * 5);
                    using var process = Process.GetProcessById(processId);
                    var actualPath = process.MainModule?.FileName;
                    if (actualPath is not null && string.Equals(Path.GetFullPath(actualPath),
                            Path.GetFullPath(expectedProcessPath), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or
                                   UnauthorizedAccessException or System.ComponentModel.Win32Exception or
                                   MarshalDirectiveException or OverflowException)
        {
            return false;
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr tcpTable, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);
}
