using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>
/// Finds installed Python interpreters without running python.exe: python.exe on PATH, the
/// PEP 514 registry keys (what the py launcher lists: python.org, Microsoft Store, Anaconda...),
/// the Python install manager's folder and pyenv-win versions.
/// </summary>
public static class PythonDetector
{
    /// <summary>
    /// Returns every distinct installation. The first item is the default one: python.exe on
    /// PATH if present, otherwise the newest registered install.
    /// Returns an empty list when Python is not installed.
    /// </summary>
    public static IReadOnlyList<RuntimeInstallation> Find()
    {
        var found = new RuntimeProbe.Collector();
        if (!OperatingSystem.IsWindows())
            return found.Items;

        var registered = ReadPep514();
        var registeredByHome = new Dictionary<string, (string Version, string Vendor)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in registered)
            registeredByHome.TryAdd(r.Home, (r.Version, r.Vendor));

        void Add(string home, string source) => found.TryAdd(home, h => Describe(h, source, registeredByHome));

        foreach (var exe in RuntimeProbe.FindOnPath("python.exe"))
        {
            // WindowsApps holds the Store's app execution aliases; with no Store Python installed
            // they only open the Store. A real Store install is also registered under PEP 514.
            if (exe.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase))
                continue;
            var home = ValidHome(Path.GetDirectoryName(AliasTarget(exe) ?? exe));
            if (home is not null)
            {
                Add(home, "PATH");
                break;
            }
        }

        var start = found.Items.Count;
        foreach (var r in registered)
            Add(r.Home, "Registry");
        found.SortNewestFirst(start);

        // Python install manager (python.org's installer from 3.14 on): %LOCALAPPDATA%\Python\pythoncore-3.14-64.
        // It does not always write PEP 514 keys.
        start = found.Items.Count;
        var managerRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Python");
        foreach (var dir in RuntimeProbe.SubDirectories(managerRoot))
        {
            var home = ValidHome(dir);
            if (home is not null)
                Add(home, "Python install manager");
        }
        found.SortNewestFirst(start);

        start = found.Items.Count;
        var pyenvRoot = Environment.GetEnvironmentVariable("PYENV_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pyenv", "pyenv-win");
        foreach (var dir in RuntimeProbe.SubDirectories(Path.Combine(pyenvRoot, "versions")))
        {
            var home = ValidHome(dir);
            if (home is not null)
                Add(home, "pyenv");
        }
        found.SortNewestFirst(start);

        return found.Items;
    }

    // PEP 514: Software\Python\<Company>\<Tag>\InstallPath, in HKCU and both HKLM views.
    [SupportedOSPlatform("windows")]
    private static List<(string Home, string Version, string Vendor)> ReadPep514()
    {
        var result = new List<(string, string, string)>();
        var roots = new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
        };
        foreach (var (hive, view) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var python = baseKey.OpenSubKey(@"SOFTWARE\Python");
                if (python is null)
                    continue;
                foreach (var companyName in python.GetSubKeyNames())
                {
                    if (companyName.Equals("PyLauncher", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        using var company = python.OpenSubKey(companyName);
                        if (company is null)
                            continue;
                        var vendor = company.GetValue("DisplayName") as string ?? companyName;
                        foreach (var tag in company.GetSubKeyNames())
                        {
                            using var tagKey = company.OpenSubKey(tag);
                            using var install = tagKey?.OpenSubKey("InstallPath");
                            var home = ValidHome(install?.GetValue(null) as string)
                                ?? ValidHome(Path.GetDirectoryName(install?.GetValue("ExecutablePath") as string ?? ""));
                            if (home is null)
                                continue;
                            var version = tagKey?.GetValue("Version") as string;
                            result.Add((home, string.IsNullOrWhiteSpace(version) ? "" : version.Trim(), vendor.Trim()));
                        }
                    }
                    catch (Exception)
                    {
                        // Unreadable company key - skip it.
                    }
                }
            }
            catch (Exception)
            {
                // Access denied or missing view - skip it.
            }
        }
        return result;
    }

    // The Python install manager puts launcher copies in %LOCALAPPDATA%\Python\bin, each next to a
    // "<name>.__target__" file holding the real interpreter's path.
    private static string? AliasTarget(string exe)
    {
        try
        {
            var marker = exe + ".__target__";
            if (!File.Exists(marker) || new FileInfo(marker).Length > 4096)
                return null;
            return RuntimeProbe.LocalFullPath(File.ReadAllText(marker).Trim());
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Returns the full path if the folder contains a real python.exe (not a launcher alias), otherwise null.</summary>
    private static string? ValidHome(string? home)
    {
        var full = RuntimeProbe.LocalFullPath(home);
        if (full is null)
            return null;
        var exe = Path.Combine(full, "python.exe");
        return File.Exists(exe) && !File.Exists(exe + ".__target__") ? full : null;
    }

    private static RuntimeInstallation Describe(string home, string source,
        Dictionary<string, (string Version, string Vendor)> registered)
    {
        registered.TryGetValue(home, out var reg);
        var exeVersion = RuntimeProbe.FileProductVersion(Path.Combine(home, "python.exe"), out var company);
        // The exe's product version has the patch number ("3.12.4"); PEP 514's Version may not.
        var version = exeVersion ?? (reg.Version is { Length: > 0 } ? reg.Version : null) ?? DllVersion(home) ?? "Unknown";
        var vendor = reg.Vendor is { Length: > 0 } ? reg.Vendor : company;
        return new RuntimeInstallation(version, vendor, home, source);
    }

    // Last resort: python312.dll next to python.exe means 3.12.
    private static string? DllVersion(string home)
    {
        try
        {
            foreach (var dll in Directory.EnumerateFiles(home, "python3*.dll"))
            {
                var digits = Path.GetFileNameWithoutExtension(dll)["python".Length..];
                if (digits.Length >= 2 && digits.All(char.IsAsciiDigit))
                    return $"{digits[0]}.{digits[1..]}";
            }
        }
        catch (Exception)
        {
            // Unreadable folder.
        }
        return null;
    }
}
