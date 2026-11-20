using WindowsUtils.Core.SystemInfo;
using WindowsUtils.Localization;

namespace WindowsUtils.Utilities;

public class StartupProgramsControl : UtilityControl
{
    public StartupProgramsControl()
    {
        var grid = CreateGrid();
        grid.Columns.Add("Name", L.T("Name"));
        grid.Columns.Add("Command", L.T("Command"));
        grid.Columns.Add("Location", L.T("Location"));
        MakeUnsortable(grid);

        foreach (var entry in StartupPrograms.GetEntries())
            grid.Rows.Add(entry.Name, entry.Command, entry.Location);

        var note = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Text = L.T("Read-only view of programs registered to start with Windows (registry Run keys, 64-bit view)."),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
        };

        Controls.Add(grid);
        Controls.Add(note);
    }
}
