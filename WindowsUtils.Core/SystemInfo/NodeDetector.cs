using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>
/// Finds installed Node.js versions without running node.exe: node.exe on PATH, the Node.js
/// installer's registry key, and versions managed by nvm-windows, Volta and fnm.
/// Versions come from node.exe's file version.
/// </summary>
public static class NodeDetector
{
    /// <summary>
    /// Returns every distinct installation. The first item is the default one: node.exe on PATH
    /// if present (for nvm this is the active version), otherwise the installer's registry entry.
    /// Returns an empty list when Node.js is not installed.
    /// </summary>
    public static IReadOnlyList<RuntimeInstallation> Find()
    {
        var found = new RuntimeProbe.Collector();
        if (!OperatingSystem.IsWindows())
            return found.Items;

        void Add(string? home, string source)
        {
            var valid = ValidHome(home);
            if (valid is not null)
                found.TryAdd(valid, h => Describe(h, source));
        }

        var onPath = RuntimeProbe.FindOnPath("node.exe").FirstOrDefault();
        if (onPath is not null)
            Add(Path.GetDirectoryName(onPath), "PATH");

        Add(ReadInstallerKey(), "Registry");

        var start = found.Items.Count;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var managers = new (string Source, string? Root, string SubPath)[]
        {
            // nvm-windows: <NVM_HOME>\v20.11.1\node.exe
            ("nvm", Environment.GetEnvironmentVariable("NVM_HOME"), ""),
            // Volta: %LOCALAPPDATA%\Volta\tools\image\node\20.11.1\node.exe
            ("Volta", Path.Combine(localAppData, "Volta", "tools", "image", "node"), ""),
            // fnm: <FNM_DIR>\node-versions\v20.11.1\installation\node.exe
            ("fnm", Path.Combine(Environment.GetEnvironmentVariable("FNM_DIR") ?? Path.Combine(appData, "fnm"), "node-versions"), "installation"),
        };
        foreach (var (source, root, subPath) in managers)
        {
            foreach (var dir in RuntimeProbe.SubDirectories(root))
                Add(Path.Combine(dir, subPath), source);
        }
        found.SortNewestFirst(start);

        return found.Items;
    }

    // The official MSI writes HKLM\SOFTWARE\Node.js\InstallPath.
    [SupportedOSPlatform("windows")]
    private static string? ReadInstallerKey()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Node.js");
                if (key?.GetValue("InstallPath") is string path && !string.IsNullOrWhiteSpace(path))
                    return path;
            }
            catch (Exception)
            {
                // Access denied or missing view - skip it.
            }
        }
        return null;
    }

    /// <summary>Returns the full path if the folder contains node.exe, otherwise null.</summary>
    private static string? ValidHome(string? home)
    {
        var full = RuntimeProbe.LocalFullPath(home);
        return full is not null && File.Exists(Path.Combine(full, "node.exe")) ? full : null;
    }

    private static RuntimeInstallation Describe(string home, string source)
    {
        var version = RuntimeProbe.FileProductVersion(Path.Combine(home, "node.exe"), out _);
        if (version is null)
        {
            // Version managers name the folder after the version (v20.11.1 or 20.11.1).
            var folder = Path.GetFileName(source == "fnm" ? Path.GetDirectoryName(home) : home) ?? "";
            version = folder.TrimStart('v').Length > 0 && char.IsAsciiDigit(folder.TrimStart('v')[0]) ? folder.TrimStart('v') : "Unknown";
        }
        return new RuntimeInstallation(version.TrimStart('v'), "", home, source);
    }
}
