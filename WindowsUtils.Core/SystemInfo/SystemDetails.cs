using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>
/// Machine details that <see cref="Environment"/> alone does not expose: physical memory,
/// CPU model name, uptime, the system directory and the primary screen size. Read-only;
/// Windows-only values return empty/zero elsewhere instead of throwing.
/// </summary>
public static class SystemDetails
{
    /// <summary>Physical memory in bytes; (0, 0) when the query fails or on non-Windows.</summary>
    public static (ulong Total, ulong Available) Memory()
    {
        if (!OperatingSystem.IsWindows())
            return (0, 0);
        var status = new MEMORYSTATUSEX();
        return GlobalMemoryStatusEx(status) ? (status.ullTotalPhys, status.ullAvailPhys) : (0, 0);
    }

    /// <summary>CPU model name from the registry; "Unknown" when unavailable.</summary>
    public static string CpuName()
    {
        if (!OperatingSystem.IsWindows())
            return "Unknown";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString") as string;
            return string.IsNullOrWhiteSpace(name) ? "Unknown" : name.Trim();
        }
        catch (Exception)
        {
            return "Unknown";
        }
    }

    /// <summary>Time since the machine booted.</summary>
    public static TimeSpan Uptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>The Windows system directory (e.g. C:\Windows\System32); empty elsewhere.</summary>
    public static string GetSystemDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return string.Empty;
        return Environment.SystemDirectory;
    }

    /// <summary>Primary screen size in pixels; (0, 0) on non-Windows.</summary>
    public static (int Width, int Height) ScreenSize()
    {
        if (!OperatingSystem.IsWindows())
            return (0, 0);
        return (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(MEMORYSTATUSEX lpBuffer);
}
