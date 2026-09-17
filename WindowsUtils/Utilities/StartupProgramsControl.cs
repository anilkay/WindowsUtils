using Microsoft.Win32;

namespace WindowsUtils.Utilities;

public class StartupProgramsControl : UtilityControl
{
    private static readonly (string Label, RegistryHive Hive, string Path)[] Locations =
    [
        (@"HKCU\...\Run", RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (@"HKCU\...\RunOnce", RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        (@"HKLM\...\Run", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (@"HKLM\...\RunOnce", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
    ];

    public StartupProgramsControl()
    {
        var grid = CreateGrid();
        grid.Columns.Add("Name", "Name");
        grid.Columns.Add("Command", "Command");
        grid.Columns.Add("Location", "Location");
        MakeUnsortable(grid);

        foreach (var (label, hive, path) in Locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(path);
                if (key is null)
                    continue;

                foreach (var name in key.GetValueNames())
                    grid.Rows.Add(name, key.GetValue(name)?.ToString() ?? "", label);
            }
            catch (Exception)
            {
                // Access denied or other registry errors - skip that location.
            }
        }

        var note = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Text = "Read-only view of programs registered to start with Windows (registry Run keys, 64-bit view).",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
        };

        Controls.Add(grid);
        Controls.Add(note);
    }
}
