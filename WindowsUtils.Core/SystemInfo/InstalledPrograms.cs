using System.Globalization;
using Microsoft.Win32;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>An installed program from the registry Uninstall keys. Size and date are missing when the installer did not record them.</summary>
public sealed record InstalledProgram(
    string Name,
    string Version,
    string Publisher,
    DateTime? InstallDate,
    long? SizeBytes,
    string InstallLocation,
    string Scope);

/// <summary>
/// Read-only list of installed programs, as shown in Apps / Programs and Features: the Uninstall keys of
/// HKLM (64-bit and 32-bit views) and HKCU. System components, updates and entries without a name are left out.
/// </summary>
public static class InstalledPrograms
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    // Hives are picked inside Get, behind the OperatingSystem.IsWindows() guard.
    private static readonly (string Scope, bool Machine, bool Wow64)[] Locations =
    [
        ("Machine (64-bit)", true, false),
        ("Machine (32-bit)", true, true),
        ("User", false, false),
    ];

    /// <summary>Returns the programs sorted by name; locations that cannot be read are skipped.</summary>
    public static IReadOnlyList<InstalledProgram> Get()
    {
        var programs = new List<InstalledProgram>();
        if (!OperatingSystem.IsWindows())
            return programs;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (scope, machine, wow64) in Locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                    wow64 ? RegistryView.Registry32 : RegistryView.Registry64);
                using var uninstall = baseKey.OpenSubKey(UninstallPath);
                if (uninstall is null)
                    continue;

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var key = uninstall.OpenSubKey(subKeyName);
                        var program = key is null ? null : Read(key, scope);
                        // On 32-bit Windows both views are the same key; the same app can also be listed twice.
                        if (program is not null && seen.Add($"{program.Name}|{program.Version}"))
                            programs.Add(program);
                    }
                    catch (Exception)
                    {
                        // One unreadable entry must not hide the rest.
                    }
                }
            }
            catch (Exception)
            {
                // Access denied or other registry errors - skip that location.
            }
        }
        programs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return programs;
    }

    private static InstalledProgram? Read(RegistryKey key, string scope)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var name = (key.GetValue("DisplayName") as string)?.Trim();
        if (string.IsNullOrEmpty(name))
            return null;
        // Hidden from Programs and Features: OS components and updates attached to a parent program.
        if (key.GetValue("SystemComponent") is int and 1 || key.GetValue("ParentKeyName") is string { Length: > 0 })
            return null;
        if (key.GetValue("ReleaseType") is string release && release is "Update" or "Hotfix" or "Security Update")
            return null;

        // EstimatedSize is a DWORD in KB.
        long? size = key.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : null;
        return new InstalledProgram(
            name,
            (key.GetValue("DisplayVersion") as string)?.Trim() ?? "",
            (key.GetValue("Publisher") as string)?.Trim() ?? "",
            ParseDate(key.GetValue("InstallDate") as string),
            size,
            (key.GetValue("InstallLocation") as string)?.Trim().Trim('"') ?? "",
            scope);
    }

    // Normally yyyyMMdd; some installers write other formats, which are tried before giving up.
    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date) && date.Year > 1990)
            return date.Date;
        return null;
    }
}
