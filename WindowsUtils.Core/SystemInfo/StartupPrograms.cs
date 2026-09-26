using Microsoft.Win32;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>A program registered to start with Windows.</summary>
public sealed record StartupEntry(string Name, string Command, string Location);

/// <summary>Read-only view of the registry Run/RunOnce keys (64-bit view).</summary>
public static class StartupPrograms
{
    // Hives are picked inside GetEntries, behind the OperatingSystem.IsWindows() guard.
    private static readonly (string Label, bool Machine, string Path)[] Locations =
    [
        (@"HKCU\...\Run", false, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (@"HKCU\...\RunOnce", false, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        (@"HKLM\...\Run", true, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (@"HKLM\...\RunOnce", true, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
    ];

    /// <summary>Returns all entries; locations that cannot be read (access denied) are skipped.</summary>
    public static IReadOnlyList<StartupEntry> GetEntries()
    {
        var entries = new List<StartupEntry>();
        if (!OperatingSystem.IsWindows())
            return entries;

        foreach (var (label, machine, path) in Locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(path);
                if (key is null)
                    continue;

                foreach (var name in key.GetValueNames())
                    entries.Add(new StartupEntry(name, key.GetValue(name)?.ToString() ?? "", label));
            }
            catch (Exception)
            {
                // Access denied or other registry errors - skip that location.
            }
        }
        return entries;
    }
}
