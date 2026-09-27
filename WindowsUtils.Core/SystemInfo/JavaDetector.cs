using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>
/// Finds installed Java runtimes (JDK/JRE) without running java.exe: JAVA_HOME, the registry
/// keys written by common JDK/JRE installers, then java.exe on PATH. Versions come from the
/// installation's "release" file, falling back to java.exe's file version.
/// </summary>
public static class JavaDetector
{
    // Registry roots used by common vendors (under HKLM\SOFTWARE, 64- and 32-bit views).
    // Oracle, Amazon Corretto and older OpenJDK builds use JavaSoft; the others use their own
    // key with the install folder in a value such as JavaHome, InstallationPath or Path.
    private static readonly string[] RegistryRoots =
    [
        @"JavaSoft\JDK",
        @"JavaSoft\JRE",
        @"JavaSoft\Java Development Kit",
        @"JavaSoft\Java Runtime Environment",
        @"Eclipse Adoptium",
        @"Eclipse Foundation",
        @"AdoptOpenJDK",
        @"Azul Systems\Zulu",
        @"BellSoft",
        @"Microsoft\JDK",
        @"Amazon Corretto",
        @"Semeru",
    ];

    private static readonly string[] HomeValueNames = ["JavaHome", "InstallationPath", "Path"];

    /// <summary>
    /// Returns every distinct installation. The first item is the default one: JAVA_HOME if it
    /// points at a valid install, otherwise java.exe on PATH, otherwise the newest registry entry.
    /// Returns an empty list when Java is not installed.
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

        Add(Environment.GetEnvironmentVariable("JAVA_HOME"), "JAVA_HOME");
        foreach (var exe in RuntimeProbe.FindOnPath("java.exe"))
        {
            var home = ValidHome(Path.GetDirectoryName(Path.GetDirectoryName(exe)));
            if (home is not null)
            {
                found.TryAdd(home, h => Describe(h, "PATH"));
                break;
            }
        }

        var fromRegistry = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                foreach (var root in RegistryRoots)
                {
                    using var key = baseKey.OpenSubKey(@"SOFTWARE\" + root);
                    if (key is not null)
                        CollectHomes(key, depth: 0, fromRegistry);
                }
            }
            catch (Exception)
            {
                // Access denied or missing view - skip it.
            }
        }

        var start = found.Items.Count;
        foreach (var home in fromRegistry)
            Add(home, "Registry");
        found.SortNewestFirst(start);
        return found.Items;
    }

    // Vendor keys nest the path 1-3 levels down, e.g. Eclipse Adoptium\JDK\21.0.4.7\hotspot\MSI.
    [SupportedOSPlatform("windows")]
    private static void CollectHomes(RegistryKey key, int depth, List<string> homes)
    {
        foreach (var name in HomeValueNames)
        {
            if (key.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value))
                homes.Add(value);
        }
        if (depth >= 4)
            return;
        foreach (var subName in key.GetSubKeyNames())
        {
            try
            {
                using var sub = key.OpenSubKey(subName);
                if (sub is not null)
                    CollectHomes(sub, depth + 1, homes);
            }
            catch (Exception)
            {
                // Unreadable subkey - skip it.
            }
        }
    }

    /// <summary>Returns the full path if the folder contains bin\java.exe, otherwise null.</summary>
    private static string? ValidHome(string? home)
    {
        var full = RuntimeProbe.LocalFullPath(home);
        return full is not null && File.Exists(Path.Combine(full, "bin", "java.exe")) ? full : null;
    }

    private static RuntimeInstallation Describe(string home, string source)
    {
        string? version = null;
        var vendor = "";
        try
        {
            var release = Path.Combine(home, "release");
            if (File.Exists(release))
            {
                foreach (var line in File.ReadLines(release))
                {
                    var eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    var key = line[..eq].Trim();
                    var value = line[(eq + 1)..].Trim().Trim('"');
                    if (key == "JAVA_VERSION")
                        version = value;
                    else if (key == "IMPLEMENTOR")
                        vendor = value;
                }
            }
        }
        catch (Exception)
        {
            // Unreadable release file - fall back to the file version.
        }

        if (string.IsNullOrEmpty(version))
        {
            version = RuntimeProbe.FileProductVersion(Path.Combine(home, "bin", "java.exe"), out var company);
            if (vendor.Length == 0)
                vendor = company;
        }

        return new RuntimeInstallation(string.IsNullOrWhiteSpace(version) ? "Unknown" : version.Trim(), vendor.Trim(), home, source);
    }
}
