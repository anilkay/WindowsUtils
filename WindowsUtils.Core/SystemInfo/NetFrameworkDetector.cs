using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>
/// Finds installed .NET Framework versions from the setup registry keys
/// (HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP). 4.5 and later are identified by the
/// Release DWORD under v4\Full; older side-by-side versions (1.1, 2.0, 3.0, 3.5) by their own keys.
/// </summary>
public static class NetFrameworkDetector
{
    private const string NdpKey = @"SOFTWARE\Microsoft\NET Framework Setup\NDP";

    // Minimum Release value for each 4.5+ version, newest first.
    // https://learn.microsoft.com/dotnet/framework/install/how-to-determine-which-versions-are-installed
    private static readonly (int Release, string Version)[] Releases =
    [
        (533320, "4.8.1"),
        (528040, "4.8"),
        (461808, "4.7.2"),
        (461308, "4.7.1"),
        (460798, "4.7"),
        (394802, "4.6.2"),
        (394254, "4.6.1"),
        (393295, "4.6"),
        (379893, "4.5.2"),
        (378675, "4.5.1"),
        (378389, "4.5"),
    ];

    // Pre-4 versions install side by side, each under its own key (also its folder name).
    private static readonly (string Key, string Version)[] Legacy =
    [
        ("v3.5", "3.5"),
        ("v3.0", "3.0"),
        ("v2.0.50727", "2.0"),
        ("v1.1.4322", "1.1"),
    ];

    /// <summary>Maps a v4\Full Release value to its version, e.g. 533320 to "4.8.1".</summary>
    public static string VersionFromRelease(int release)
    {
        foreach (var (min, version) in Releases)
        {
            if (release >= min)
                return version;
        }
        return "4.0";
    }

    /// <summary>
    /// Returns every installed version, newest first (the 4.x one, then 3.5, 3.0, 2.0, 1.1).
    /// Version is the product version (e.g. "4.8.1"), Vendor holds the build number
    /// (e.g. "4.8.09221") and Source says which registry key it came from.
    /// Returns an empty list when nothing is found.
    /// </summary>
    public static IReadOnlyList<RuntimeInstallation> Find()
    {
        var found = new List<RuntimeInstallation>();
        if (!OperatingSystem.IsWindows())
            return found;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var ndp = baseKey.OpenSubKey(NdpKey);
            if (ndp is null)
                return found;

            using (var full = OpenSubKey(ndp, @"v4\Full"))
            {
                if (full is not null)
                {
                    var version = full.GetValue("Release") is int release ? VersionFromRelease(release) : "4.0";
                    found.Add(Describe(full, version, "v4.0.30319", @"v4\Full"));
                }
            }

            foreach (var (keyName, version) in Legacy)
            {
                using var key = OpenSubKey(ndp, keyName);
                if (key is not null && key.GetValue("Install") is int install && install == 1)
                    found.Add(Describe(key, version, keyName, keyName));
            }
        }
        catch (Exception)
        {
            // Access denied or missing view - report what was found so far.
        }
        return found;
    }

    [SupportedOSPlatform("windows")]
    private static RegistryKey? OpenSubKey(RegistryKey parent, string name)
    {
        try
        {
            return parent.OpenSubKey(name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static RuntimeInstallation Describe(RegistryKey key, string version, string folder, string keyName)
    {
        if (key.GetValue("SP") is int sp && sp > 0)
            version += $" SP{sp}";
        var build = key.GetValue("Version") as string ?? "";

        var home = key.GetValue("InstallPath") as string;
        if (string.IsNullOrWhiteSpace(home))
        {
            var framework = Environment.Is64BitOperatingSystem ? "Framework64" : "Framework";
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", framework, folder);
        }

        return new RuntimeInstallation(version, build.Trim(), home.TrimEnd('\\'), $@"Registry NDP\{keyName}");
    }
}
