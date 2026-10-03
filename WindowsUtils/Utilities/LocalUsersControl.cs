using WindowsUtils.Core.SystemInfo;

namespace WindowsUtils.Utilities;

public class LocalUsersControl : UtilityControl
{
    private readonly DataGridView _grid;
    private readonly Label _note = new();

    public LocalUsersControl()
    {
        _grid = CreateGrid();
        _grid.Columns.Add("Name", "Name");
        _grid.Columns.Add("FullName", "Full name");
        _grid.Columns.Add("Status", "Status");
        _grid.Columns.Add("Type", "Type");
        _grid.Columns.Add("LastLogon", "Last logon");
        _grid.Columns.Add("PasswordSet", "Password last set");
        _grid.Columns.Add("Description", "Description");
        MakeUnsortable(_grid);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4),
        };
        var addButton = new Button { Text = "Add User...", AutoSize = true, Margin = new Padding(4, 6, 4, 4) }.AsAccent();
        var refreshButton = new Button { Text = "Refresh", AutoSize = true, Margin = new Padding(4, 6, 4, 4) };
        var deleteButton = new Button { Text = "Delete User", AutoSize = true, Margin = new Padding(4, 6, 4, 4) };
        deleteButton.Click += (_, _) => DeleteSelectedUser();
        addButton.Click += (_, _) => ShowAddUserWizard();
        refreshButton.Click += (_, _) => LoadAccounts();
        toolbar.Controls.Add(addButton);
        toolbar.Controls.Add(deleteButton);
        toolbar.Controls.Add(refreshButton);

        _note.Dock = DockStyle.Bottom;
        _note.Height = 26;
        _note.TextAlign = ContentAlignment.MiddleLeft;
        _note.Padding = new Padding(6, 0, 0, 0);
        _note.Text = "Read-only view of the local user accounts on this PC (domain accounts are not listed).";

        Controls.Add(_grid);
        Controls.Add(_note);
        Controls.Add(toolbar);

        LoadAccounts();
    }

    private void LoadAccounts()
    {
        _grid.Rows.Clear();
        try
        {
            foreach (var a in LocalUsers.GetAccounts())
            {
                var row = _grid.Rows.Add(
                    a.Name,
                    a.FullName,
                    LocalUsers.FormatStatus(a),
                    LocalUsers.FormatType(a),
                    a.LastLogon?.ToString("g") ?? "Never",
                    a.PasswordLastSet?.ToString("g") ?? "",
                    a.Description);
                if (!a.Enabled)
                    _grid.Rows[row].DefaultCellStyle.ForeColor = Theme.SubtleText;
            }
            _note.Text = "Read-only view of the local user accounts on this PC (domain accounts are not listed).";
        }
        catch (Exception ex)
        {
            _note.Text = $"Could not read user accounts: {ex.Message}";
        }
    }

    private void DeleteSelectedUser()
    {
        if (_grid.CurrentRow?.Cells[0].Value is not string name || name.Length == 0)
            return;
        if (name.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("You cannot delete the account you are currently signed in with.", "Delete User",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show($"Delete the local user '{name}'? This cannot be undone.", "Delete User",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        var result = UserManagement.DeleteUser(name);
        if (result.Success)
        {
            _note.Text = $"Account '{name}' deleted.";
            LoadAccounts();
        }
        else
        {
            _note.Text = $"Could not delete '{name}': {result.Error}";
        }
    }

    private void ShowAddUserWizard()
    {
        using var wizard = new AddUserWizardForm();
        wizard.ShowDialog(this);
        if (wizard.WasCreated)
        {
            _note.Text = $"Account '{wizard.Result?.Name}' created.";
            LoadAccounts();
        }
    }
}
