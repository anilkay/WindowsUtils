using WindowsUtils.Core.SystemInfo;

namespace WindowsUtils.Utilities;

/// <summary>Modal wizard for creating a local user account. Shows Result via ShowCreated property.</summary>
public class AddUserWizardForm : Form
{
    private readonly TextBox _nameBox = new();
    private readonly TextBox _fullNameBox = new();
    private readonly TextBox _descriptionBox = new();
    private readonly TextBox _passwordBox = new() { UseSystemPasswordChar = true };
    private readonly TextBox _confirmBox = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _neverExpiresCheck = new() { Text = "Password never expires", AutoSize = true };
    private readonly CheckBox _noPasswordChangeCheck = new() { Text = "User cannot change password", AutoSize = true };
    private readonly RadioButton _standardRadio = new() { Text = "Standard user", AutoSize = true, Checked = true };
    private readonly RadioButton _adminRadio = new() { Text = "Administrator", AutoSize = true };
    private readonly Button _createButton = new() { Text = "Create", AutoSize = true };
    private readonly Button _cancelButton = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

    /// <summary>The account to create; null when the wizard was cancelled.</summary>
    public NewLocalUser? Result { get; private set; }
    public bool WasCreated { get; private set; }

    private static void AddRow(TableLayoutPanel layout, int row, string label, Control control)
    {
        layout.Controls.Add(
            new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 2) },
            0, row);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        layout.Controls.Add(control, 1, row);
    }

    public AddUserWizardForm()
    {
        Text = "Add Local User";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 400);
        Font = Theme.BodyFont;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(layout, 0, "User name *", _nameBox);
        _nameBox.PlaceholderText = "max. 20 chars, no spaces";
        AddRow(layout, 1, "Full name", _fullNameBox);
        AddRow(layout, 2, "Description", _descriptionBox);
        AddRow(layout, 3, "Password *", _passwordBox);
        AddRow(layout, 4, "Confirm password *", _confirmBox);

        _neverExpiresCheck.Margin = new Padding(3, 6, 3, 2);
        layout.Controls.Add(_neverExpiresCheck, 1, 5);
        _noPasswordChangeCheck.Margin = new Padding(3, 2, 3, 2);
        layout.Controls.Add(_noPasswordChangeCheck, 1, 6);

        layout.Controls.Add(new Label
        {
            Text = "Account type:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 2),
        }, 0, 7);

        var typePanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        _standardRadio.Margin = new Padding(3, 8, 3, 2);
        _adminRadio.Margin = new Padding(16, 8, 3, 2);
        typePanel.Controls.Add(_standardRadio);
        typePanel.Controls.Add(_adminRadio);
        layout.Controls.Add(typePanel, 1, 7);

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 12, 0),
            ForeColor = Theme.SubtleText,
            Text = "Creating an account requires Administrator privileges.",
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(8),
        };
        _createButton.Margin = new Padding(4, 6, 4, 4);
        _cancelButton.Margin = new Padding(4, 6, 4, 4);
        buttons.Controls.Add(_createButton);
        buttons.Controls.Add(_cancelButton);

        Controls.Add(layout);
        Controls.Add(status);
        Controls.Add(buttons);

        AcceptButton = _createButton;
        CancelButton = _cancelButton;

        _createButton.Click += (_, _) => TryCreate(status);
        Theme.Apply(this);
        _createButton.AsAccent();
    }

    private void TryCreate(Label status)
    {
        var user = new NewLocalUser(
            Name: _nameBox.Text.Trim(),
            Password: _passwordBox.Text,
            FullName: _fullNameBox.Text.Trim(),
            Description: _descriptionBox.Text.Trim(),
            PasswordNeverExpires: _neverExpiresCheck.Checked,
            DisablePasswordChange: _noPasswordChangeCheck.Checked,
            IsAdmin: _adminRadio.Checked);

        if (user.Password != _confirmBox.Text)
        {
            MessageBox.Show("The passwords do not match.", "Add Local User",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _confirmBox.Focus();
            return;
        }

        _createButton.Enabled = false;
        status.ForeColor = Theme.SubtleText;
        status.Text = "Creating account...";
        Task.Run(() => UserManagement.AddUser(user))
            .ContinueWith(t =>
            {
                var result = t.IsFaulted ? new CreateResult(false, t.Exception?.GetBaseException().Message) : t.Result;
                if (result.Success)
                {
                    Result = user;
                    WasCreated = true;
                    DialogResult = DialogResult.OK;
                    Close();
                }
                else
                {
                    _createButton.Enabled = true;
                    status.ForeColor = Theme.Danger;
                    status.Text = result.Error ?? "Unknown error.";
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
