using WindowsUtils.Core.SystemInfo;
using WindowsUtils.Localization;

namespace WindowsUtils.Utilities;

public class LocalUsersControl : UtilityControl
{
    private readonly DataGridView _grid;
    private readonly Label _note = new();

    public LocalUsersControl()
    {
        _grid = CreateGrid();
        _grid.Columns.Add("Name", L.T("Name"));
        _grid.Columns.Add("FullName", L.T("Full name"));
        _grid.Columns.Add("Status", L.T("Status"));
        _grid.Columns.Add("Type", L.T("Type"));
        _grid.Columns.Add("LastLogon", L.T("Last logon"));
        _grid.Columns.Add("PasswordSet", L.T("Password last set"));
        _grid.Columns.Add("Description", L.T("Description"));
        MakeUnsortable(_grid);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4),
        };
        var addButton = new Button { Text = L.T("Add User..."), AutoSize = true, Margin = new Padding(4, 6, 4, 4) }.AsAccent();
        var refreshButton = new Button { Text = L.T("Refresh"), AutoSize = true, Margin = new Padding(4, 6, 4, 4) };
        var deleteButton = new Button { Text = L.T("Delete User"), AutoSize = true, Margin = new Padding(4, 6, 4, 4) };
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
        _note.Text = L.T("Read-only view of the local user accounts on this PC (domain accounts are not listed).");

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
                    L.V(LocalUsers.FormatStatus(a)),
                    L.V(LocalUsers.FormatType(a)),
                    a.LastLogon?.ToString("g") ?? L.T("Never"),
                    a.PasswordLastSet?.ToString("g") ?? "",
                    a.Description);
                if (!a.Enabled)
                    _grid.Rows[row].DefaultCellStyle.ForeColor = Theme.SubtleText;
            }
            _note.Text = L.T("Read-only view of the local user accounts on this PC (domain accounts are not listed).");
        }
        catch (Exception ex)
        {
            _note.Text = L.F("Could not read user accounts: {0}", ex.Message);
        }
    }

    private void DeleteSelectedUser()
    {
        if (_grid.CurrentRow?.Cells[0].Value is not string name || name.Length == 0)
            return;
        if (name.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(L.T("You cannot delete the account you are currently signed in with."), L.T("Delete User"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(L.F("Delete the local user '{0}'? This cannot be undone.", name), L.T("Delete User"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        var result = UserManagement.DeleteUser(name);
        if (result.IsSuccess)
        {
            _note.Text = L.F("Account '{0}' deleted.", name);
            LoadAccounts();
        }
        else
        {
            _note.Text = L.F("Could not delete '{0}': {1}", name, result.Errors[0].Message);
        }
    }

    private void ShowAddUserWizard()
    {
        using var wizard = new AddUserWizardForm();
        wizard.ShowDialog(this);
        if (wizard.WasCreated)
        {
            _note.Text = L.F("Account '{0}' created.", wizard.Result?.Name);
            LoadAccounts();
        }
    }
}
