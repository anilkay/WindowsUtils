using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>A Linux distribution registered with WSL (Version is 1 or 2).</summary>
public sealed record WslDistribution(string Name, int Version);

/// <summary>
/// Read-only WSL status. The distributions and their versions come from the current user's
/// Lxss registry key; <paramref name="Wsl2Runtime"/> says whether a WSL2 kernel or the
/// standalone WSL package is present. Nothing is started or changed.
/// </summary>
public sealed record WslInfo(int? DefaultVersion, bool Wsl2Runtime, IReadOnlyList<WslDistribution> Distributions)
{
    public bool HasWsl2 => Wsl2Runtime || DefaultVersion == 2 || Distributions.Any(d => d.Version == 2);

    public bool IsInstalled => HasWsl2 || DefaultVersion is not null || Distributions.Count > 0;

    /// <summary>One-line summary for the System Information screen and the AI tool.</summary>
    public string Display
    {
        get
        {
            if (!IsInstalled)
                return "Not installed";

            var text = HasWsl2 ? "WSL2 available" : "WSL1 only";
            if (DefaultVersion is int version)
                text += $" (default version {version})";
            text += Distributions.Count == 0
                ? "; no distributions installed"
                : "; " + string.Join(", ", Distributions.Select(d => $"{d.Name} (WSL{d.Version})"));
            return text;
        }
    }
}

/// <summary>
/// Finds WSL and WSL2 without running wsl.exe: distro names, their WSL version and the default
/// version live under HKCU\...\Lxss, so this is instant and works for Store and inbox installs.
/// </summary>
public static class WslDetector
{
    private const string LxssKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Lxss";

    public static WslInfo Get()
    {
        if (!OperatingSystem.IsWindows())
            return new WslInfo(null, false, []);

        var (defaultVersion, distributions) = ReadLxss();
        return new WslInfo(defaultVersion, HasWsl2Runtime(), distributions);
    }

    [SupportedOSPlatform("windows")]
    private static (int? DefaultVersion, List<WslDistribution> Distributions) ReadLxss()
    {
        var distributions = new List<WslDistribution>();
        int? defaultVersion = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LxssKey);
            if (key is null)
                return (null, distributions);

            if (key.GetValue("DefaultVersion") is int version && version is 1 or 2)
                defaultVersion = version;

            foreach (var id in key.GetSubKeyNames())
            {
                using var distro = key.OpenSubKey(id);
                if (distro?.GetValue("DistributionName") is not string name || string.IsNullOrWhiteSpace(name))
                    continue;
                var distroVersion = distro.GetValue("Version") is int v && v is 1 or 2
                    ? v
                    : defaultVersion ?? 1;
                distributions.Add(new WslDistribution(name.Trim(), distroVersion));
            }
        }
        catch (Exception)
        {
            // Access denied or a missing key - report what was read so far.
        }
        return (defaultVersion, distributions.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static bool HasWsl2Runtime()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (File.Exists(Path.Combine(windows, "System32", "lxss", "tools", "kernel")))
            return true;

        // WSL 2.x ships as a standalone package (Store/MSI) outside Windows.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return File.Exists(Path.Combine(programFiles, "WSL", "wsl.exe"))
            || File.Exists(Path.Combine(programFiles, "WSL", "tools", "kernel"));
    }
}
