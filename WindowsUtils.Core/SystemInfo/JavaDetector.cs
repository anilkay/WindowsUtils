using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>A Java installation (JDK or JRE) found on this PC.</summary>
/// <param name="Version">e.g. "21.0.4", or "Unknown" if it cannot be read.</param>
/// <param name="Vendor">e.g. "Eclipse Adoptium", or "" if unknown.</param>
/// <param name="Home">Installation folder (the one that contains bin\java.exe).</param>
/// <param name="Source">Where it was found: JAVA_HOME, PATH or Registry.</param>
public sealed record JavaInstallation(string Version, string Vendor, string Home, string Source);

/// <summary>
/// Finds installed Java runtimes without running java.exe: JAVA_HOME, the registry keys
/// written by common JDK/JRE installers, then java.exe on PATH. Versions come from the
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
    public static IReadOnlyList<JavaInstallation> Find()
    {
        var result = new List<JavaInstallation>();
        if (!OperatingSystem.IsWindows())
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? home, string source)
        {
            var normalized = NormalizeHome(home);
            if (normalized is not null && seen.Add(normalized))
                result.Add(Describe(normalized, source));
        }

        Add(Environment.GetEnvironmentVariable("JAVA_HOME"), "JAVA_HOME");

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

        Add(FindOnPath(), "PATH");

        var registryInstalls = new List<JavaInstallation>();
        foreach (var home in fromRegistry)
        {
            var normalized = NormalizeHome(home);
            if (normalized is not null && seen.Add(normalized))
                registryInstalls.Add(Describe(normalized, "Registry"));
        }
        result.AddRange(registryInstalls.OrderByDescending(i => ParseVersion(i.Version)));
        return result;
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

    private static string? FindOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var dir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(entry.Trim('"')));
                // Local drives only: probing a UNC PATH entry can hang or send credentials.
                if (!IsLocalDrivePath(dir))
                    continue;
                var exe = Path.Combine(dir, "java.exe");
                if (!File.Exists(exe))
                    continue;
                // The Oracle installer adds a javapath folder of links; resolve them to the real install.
                var target = new FileInfo(exe).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? exe;
                var home = NormalizeHome(Path.GetDirectoryName(Path.GetDirectoryName(target)));
                if (home is not null)
                    return home;
            }
            catch (Exception)
            {
                // Malformed or unreadable PATH entry - skip it.
            }
        }
        return null;
    }

    /// <summary>Returns the full path if the folder contains bin\java.exe, otherwise null.</summary>
    private static string? NormalizeHome(string? home)
    {
        if (string.IsNullOrWhiteSpace(home))
            return null;
        try
        {
            var full = Path.GetFullPath(home.Trim().Trim('"')).TrimEnd('\\');
            if (!IsLocalDrivePath(full))
                return null;
            return File.Exists(Path.Combine(full, "bin", "java.exe")) ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsLocalDrivePath(string path) =>
        path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';

    private static JavaInstallation Describe(string home, string source)
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
            try
            {
                var info = FileVersionInfo.GetVersionInfo(Path.Combine(home, "bin", "java.exe"));
                version = info.ProductVersion ?? info.FileVersion;
                if (vendor.Length == 0)
                    vendor = info.CompanyName ?? "";
            }
            catch (Exception)
            {
                // Ignore; version stays unknown.
            }
        }

        return new JavaInstallation(string.IsNullOrWhiteSpace(version) ? "Unknown" : version.Trim(), vendor.Trim(), home, source);
    }

    // Java versions look like "1.8.0_402" or "21.0.4"; compare the numeric parts in order.
    private static Version ParseVersion(string version)
    {
        var parts = version.Split(['.', '_', '+', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var n) ? n : 0)
            .Concat([0, 0, 0, 0])
            .Take(4)
            .ToArray();
        if (parts[0] == 1 && parts[1] >= 2)
            parts = [parts[1], 0, parts[2], parts[3]]; // 1.8.0_402 -> 8.0.0.402
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }

    /// <summary>Short display text, e.g. "21.0.4 (Eclipse Adoptium)".</summary>
    public static string Format(JavaInstallation java) =>
        java.Vendor.Length > 0 ? $"{java.Version} ({java.Vendor})" : java.Version;
}
