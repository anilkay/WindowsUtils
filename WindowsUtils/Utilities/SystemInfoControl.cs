using System.Runtime.InteropServices;
using Microsoft.Win32;
using WindowsUtils.Core.SystemInfo;

namespace WindowsUtils.Utilities;

public class SystemInfoControl : UtilityControl
{
    public SystemInfoControl()
    {
        var grid = CreateGrid();
        grid.Columns.Add("Property", "Property");
        grid.Columns.Add("Value", "Value");
        MakeUnsortable(grid);

        foreach (var (name, value) in Collect())
            grid.Rows.Add(name, value);
        AddRuntimeRows(grid, "Java", JavaDetector.Find());
        AddRuntimeRows(grid, "Python", PythonDetector.Find());
        AddRuntimeRows(grid, "Node.js", NodeDetector.Find());

        Controls.Add(grid);
    }

    private static void AddRuntimeRows(DataGridView grid, string name, IReadOnlyList<RuntimeInstallation> installs)
    {
        if (installs.Count == 0)
        {
            var row = grid.Rows[grid.Rows.Add(name, "Not installed")];
            row.Cells[1].Style.ForeColor = Theme.SubtleText;
            return;
        }

        var main = installs[0];
        grid.Rows.Add(name, main.Display);
        grid.Rows.Add($"{name} Location", $"{main.Home}  ({main.Source})");
        if (installs.Count > 1)
            grid.Rows.Add($"Other {name} Installs", string.Join(", ", installs.Skip(1).Select(i => i.Display)));
    }

    private static IEnumerable<(string Name, string Value)> Collect()
    {
        yield return ("Operating System", RuntimeInformation.OSDescription);
        yield return ("OS Version", Environment.OSVersion.VersionString);
        yield return ("OS Architecture", RuntimeInformation.OSArchitecture.ToString());
        yield return ("Machine Name", Environment.MachineName);
        yield return ("Current User", $@"{Environment.UserDomainName}\{Environment.UserName}");
        yield return (".NET Runtime", RuntimeInformation.FrameworkDescription);
        yield return ("CPU", CpuName());
        yield return ("Logical Processors", Environment.ProcessorCount.ToString());

        var (total, available) = MemoryInfo();
        if (total > 0)
        {
            yield return ("Total RAM", FormatBytes((long)total));
            yield return ("Available RAM", FormatBytes((long)available));
            yield return ("RAM In Use", $"{100 - available * 100.0 / total:0.#}%");
        }

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        yield return ("System Uptime", $"{(int)uptime.TotalDays} days, {uptime.Hours}h {uptime.Minutes}m");
        yield return ("System Directory", Environment.SystemDirectory);
        yield return ("Screen Resolution", $"{SystemInformation.PrimaryMonitorSize.Width} x {SystemInformation.PrimaryMonitorSize.Height}");
    }

    private static string CpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString") as string;
            return string.IsNullOrWhiteSpace(name) ? "Unknown" : name.Trim();
        }
        catch
        {
            return "Unknown";
        }
    }

    private static (ulong Total, ulong Available) MemoryInfo()
    {
        var status = new MEMORYSTATUSEX();
        return GlobalMemoryStatusEx(status) ? (status.ullTotalPhys, status.ullAvailPhys) : (0, 0);
    }

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
