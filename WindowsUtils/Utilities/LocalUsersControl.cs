using WindowsUtils.Core.SystemInfo;

namespace WindowsUtils.Utilities;

public class LocalUsersControl : UtilityControl
{
    public LocalUsersControl()
    {
        var grid = CreateGrid();
        grid.Columns.Add("Name", "Name");
        grid.Columns.Add("FullName", "Full name");
        grid.Columns.Add("Status", "Status");
        grid.Columns.Add("Type", "Type");
        grid.Columns.Add("LastLogon", "Last logon");
        grid.Columns.Add("PasswordSet", "Password last set");
        grid.Columns.Add("Description", "Description");
        MakeUnsortable(grid);

        var note = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Text = "Read-only view of the local user accounts on this PC (domain accounts are not listed).",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
        };

        try
        {
            foreach (var a in LocalUsers.GetAccounts())
            {
                var row = grid.Rows.Add(
                    a.Name,
                    a.FullName,
                    LocalUsers.FormatStatus(a),
                    LocalUsers.FormatType(a),
                    a.LastLogon?.ToString("g") ?? "Never",
                    a.PasswordLastSet?.ToString("g") ?? "",
                    a.Description);
                if (!a.Enabled)
                    grid.Rows[row].DefaultCellStyle.ForeColor = Theme.SubtleText;
            }
        }
        catch (Exception ex)
        {
            note.Text = $"Could not read user accounts: {ex.Message}";
        }

        Controls.Add(grid);
        Controls.Add(note);
    }
}
