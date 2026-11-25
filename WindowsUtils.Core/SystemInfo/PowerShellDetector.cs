using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>
/// Finds installed PowerShell versions without running them: pwsh.exe on PATH and under
/// %ProgramFiles%\PowerShell, Microsoft Store packages, plus Windows PowerShell 5.1.
/// Windows PowerShell's file version is the OS build, so its version comes from the
/// PowerShellEngine registry key.
/// </summary>
public static class PowerShellDetector
{
    // The package registration carries the real install folder and a version in the key name,
    // which works even though the WindowsApps alias on PATH is only a reparse point.
    private const string PackageRepositoryKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>
    /// Returns every distinct installation. PowerShell 7+ comes first (PATH, Microsoft Store,
    /// then Program Files, newest first), then Windows PowerShell 5.1. Returns an empty list on
    /// non-Windows.
    /// </summary>
    public static IReadOnlyList<RuntimeInstallation> Find()
    {
        var found = new RuntimeProbe.Collector();
        if (!OperatingSystem.IsWindows())
            return found.Items;

        foreach (var exe in RuntimeProbe.FindOnPath("pwsh.exe"))
        {
            // WindowsApps holds the Store's app execution aliases; they have no version and no
            // real install folder. The Store package is added from its registration below.
            if (exe.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase))
                continue;
            var home = ValidHome(Path.GetDirectoryName(exe));
            if (home is not null)
            {
                found.TryAdd(home, h => Describe(h, "PATH"));
                break;
            }
        }

        foreach (var install in ReadStorePackages())
            found.TryAdd(install.Home, _ => install);

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

    // Microsoft Store installs register per user under the package repository. The key name is
    // "<PackageName>_<Version>_<Arch>_<ResourceId>_<PublisherId>"; PackageRootFolder is the real
    // WindowsApps folder, which a normal user cannot enumerate but can report.
    [SupportedOSPlatform("windows")]
    private static List<RuntimeInstallation> ReadStorePackages()
    {
        var result = new List<RuntimeInstallation>();
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(PackageRepositoryKey);
            if (packages is null)
                return result;

            var windowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

            foreach (var fullName in packages.GetSubKeyNames())
            {
                var packageName = fullName.Split('_', 2)[0];
                if (!packageName.Equals("Microsoft.PowerShell", StringComparison.OrdinalIgnoreCase)
                    && !packageName.Equals("Microsoft.PowerShellPreview", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var package = packages.OpenSubKey(fullName);
                // Fall back to the default package path when the value is missing; the version
                // from the key name still identifies the install.
                var root = package?.GetValue("PackageRootFolder") as string
                    ?? Path.Combine(windowsApps, fullName);
                var home = RuntimeProbe.LocalFullPath(root);
                if (home is null)
                    continue;
                result.Add(DescribeStore(fullName, home));
            }
        }
        catch (Exception)
        {
            // Access denied or a missing key - report what was read so far.
        }
        return result;
    }

    private static RuntimeInstallation DescribeStore(string packageFullName, string home)
    {
        var version = RuntimeProbe.FileProductVersion(Path.Combine(home, "pwsh.exe"), out _)
            ?? PackageVersion(packageFullName);
        return new RuntimeInstallation(NormalizeVersion(version), "PowerShell", home, "Microsoft Store");
    }

    // "Microsoft.PowerShell_7.5.4.0_x64__8wekyb3d8bbwe" -> "7.5.4.0"
    private static string PackageVersion(string packageFullName)
    {
        var parts = packageFullName.Split('_');
        return parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "Unknown";
    }

    private static RuntimeInstallation Describe(string home, string source)
    {
        var version = RuntimeProbe.FileProductVersion(Path.Combine(home, "pwsh.exe"), out _) ?? "Unknown";
        return new RuntimeInstallation(NormalizeVersion(version), "PowerShell", home, source);
    }

    private static string NormalizeVersion(string version)
    {
        // Some builds append the commit to ProductVersion: "7.6.9 SHA: 3a0b9fb...".
        var token = version.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        // Store package versions have a fourth part (7.5.4.0); show the usual three-part form.
        if (Version.TryParse(token, out var parsed) && parsed.Build >= 0 && parsed.Revision == 0)
            return parsed.ToString(3);
        return token;
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
