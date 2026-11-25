using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>
/// Finds installed PowerShell versions without running them: pwsh.exe on PATH and under
/// %ProgramFiles%\PowerShell, plus Windows PowerShell 5.1. Windows PowerShell's file version is
/// the OS build, so its version comes from the PowerShellEngine registry key.
/// </summary>
public static class PowerShellDetector
{
    /// <summary>
    /// Returns every distinct installation. PowerShell 7+ comes first (PATH, then Program Files,
    /// newest first), then Windows PowerShell 5.1. Returns an empty list on non-Windows.
    /// </summary>
    public static IReadOnlyList<RuntimeInstallation> Find()
    {
        var found = new RuntimeProbe.Collector();
        if (!OperatingSystem.IsWindows())
            return found.Items;

        foreach (var exe in RuntimeProbe.FindOnPath("pwsh.exe"))
        {
            // WindowsApps holds the Store's app execution aliases; the version cannot be read
            // from the alias. A real Store install is not in Program Files either, so skip it.
            if (exe.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase))
                continue;
            var home = ValidHome(Path.GetDirectoryName(exe));
            if (home is not null)
            {
                found.TryAdd(home, h => Describe(h, "PATH"));
                break;
            }
        }

        var start = found.Items.Count;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var dir in RuntimeProbe.SubDirectories(Path.Combine(programFiles, "PowerShell")))
        {
            var home = ValidHome(dir);
            if (home is not null)
                found.TryAdd(home, h => Describe(h, "Program Files"));
        }
        found.SortNewestFirst(start);

        var windowsPowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0");
        if (Directory.Exists(windowsPowerShell))
            found.TryAdd(windowsPowerShell, DescribeWindowsPowerShell);

        return found.Items;
    }

    /// <summary>Returns the full path if the folder contains pwsh.exe, otherwise null.</summary>
    private static string? ValidHome(string? home)
    {
        var full = RuntimeProbe.LocalFullPath(home);
        return full is not null && File.Exists(Path.Combine(full, "pwsh.exe")) ? full : null;
    }

    private static RuntimeInstallation Describe(string home, string source)
    {
        var version = RuntimeProbe.FileProductVersion(Path.Combine(home, "pwsh.exe"), out _) ?? "Unknown";
        // Some builds append the commit to ProductVersion: "7.6.9 SHA: 3a0b9fb...".
        var displayVersion = version.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return new RuntimeInstallation(displayVersion, "PowerShell", home, source);
    }

    [SupportedOSPlatform("windows")]
    private static RuntimeInstallation DescribeWindowsPowerShell(string home) =>
        new(WindowsPowerShellVersion() ?? "5.1", "Windows PowerShell", home, "Windows");

    // powershell.exe's file version is the Windows build, but the engine key has the real
    // PowerShell version (e.g. "5.1.26100.1882").
    [SupportedOSPlatform("windows")]
    private static string? WindowsPowerShellVersion()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\PowerShell\3\PowerShellEngine");
                if (key?.GetValue("PowerShellVersion") is string version && !string.IsNullOrWhiteSpace(version))
                    return version.Trim();
            }
            catch (Exception)
            {
                // Access denied or missing view - try the next one.
            }
        }
        return null;
    }
}
