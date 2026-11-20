using System.Security.Cryptography;
using WindowsUtils.Core.Hashing;
using WindowsUtils.Localization;

namespace WindowsUtils.Utilities;

public class FileHashControl : UtilityControl
{
    private readonly TextBox _pathBox = new();
    private readonly CheckBox _md5Check = new() { Text = "MD5", Checked = true, AutoSize = true };
    private readonly CheckBox _sha1Check = new() { Text = "SHA-1", Checked = true, AutoSize = true };
    private readonly CheckBox _sha256Check = new() { Text = "SHA-256", Checked = true, AutoSize = true };
    private readonly CheckBox _sha512Check = new() { Text = "SHA-512", Checked = true, AutoSize = true };
    private readonly TextBox _md5Box = MakeReadOnlyBox();
    private readonly TextBox _sha1Box = MakeReadOnlyBox();
    private readonly TextBox _sha256Box = MakeReadOnlyBox();
    private readonly TextBox _sha512Box = MakeReadOnlyBox();
    private readonly TextBox _verifyBox = new();
    private readonly Label _verifyLabel = new();
    private readonly Button _computeButton = new();
    private CancellationTokenSource? _cts;

    public FileHashControl()
    {
        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4),
        };

        _pathBox.PlaceholderText = L.T("Select a file...");
        _pathBox.Width = 420;
        _pathBox.Margin = new Padding(4, 8, 4, 4);

        var browseButton = new Button { Text = L.T("Browse..."), AutoSize = true, Margin = new Padding(4, 6, 4, 4) };
        _computeButton.Text = L.T("Compute Hashes");
        _computeButton.AutoSize = true;
        _computeButton.AsAccent();
        _computeButton.Margin = new Padding(4, 6, 4, 4);

        browseButton.Click += (_, _) => Browse();
        _computeButton.Click += async (_, _) => await ComputeAsync();

        topPanel.Controls.Add(_pathBox);
        topPanel.Controls.Add(browseButton);
        topPanel.Controls.Add(_computeButton);

        var optionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 4, 4, 4),
        };
        optionsPanel.Controls.Add(new Label { Text = L.T("Algorithms:"), AutoSize = true, Margin = new Padding(4, 4, 4, 4) });
        optionsPanel.Controls.Add(_md5Check);
        optionsPanel.Controls.Add(_sha1Check);
        optionsPanel.Controls.Add(_sha256Check);
        optionsPanel.Controls.Add(_sha512Check);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            Padding = new Padding(8),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        AddRow(layout, 0, "MD5", _md5Box);
        AddRow(layout, 1, "SHA-1", _sha1Box);
        AddRow(layout, 2, "SHA-256", _sha256Box);
        AddRow(layout, 3, "SHA-512", _sha512Box);

        _verifyBox.PlaceholderText = L.T("Paste a hash here to verify...");
        _verifyBox.TextChanged += (_, _) => Verify();
        AddRow(layout, 4, L.T("Verify"), _verifyBox);

        _verifyLabel.Dock = DockStyle.Fill;
        _verifyLabel.TextAlign = ContentAlignment.MiddleLeft;
        _verifyLabel.Font = Theme.SemiboldFont;
        layout.Controls.Add(_verifyLabel, 0, 5);
        layout.SetColumnSpan(_verifyLabel, 2);

        Controls.Add(layout);
        Controls.Add(optionsPanel);
        Controls.Add(topPanel);
    }

    private static TextBox MakeReadOnlyBox() => new() { ReadOnly = true, BackColor = SystemColors.Window, Font = Theme.MonoFont };

    private static void AddRow(TableLayoutPanel layout, int row, string label, Control control)
    {
        layout.Controls.Add(
            new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft },
            0, row);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        layout.Controls.Add(control, 1, row);
    }

    private void Browse()
    {
        using var dialog = new OpenFileDialog { Filter = L.T("All files (*.*)|*.*") };
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _pathBox.Text = dialog.FileName;
            _ = ComputeAsync();
        }
    }

    private async Task ComputeAsync()
    {
        var path = _pathBox.Text.Trim();
        if (!File.Exists(path))
        {
            MessageBox.Show(L.T("Select an existing file first."), L.T("File Hash"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var selected = new List<HashAlgorithmName>(4);
        if (_md5Check.Checked)
            selected.Add(HashAlgorithmName.MD5);
        if (_sha1Check.Checked)
            selected.Add(HashAlgorithmName.SHA1);
        if (_sha256Check.Checked)
            selected.Add(HashAlgorithmName.SHA256);
        if (_sha512Check.Checked)
            selected.Add(HashAlgorithmName.SHA512);
        if (selected.Count == 0)
        {
            MessageBox.Show(L.T("Select at least one hash algorithm."), L.T("File Hash"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _computeButton.Enabled = false;
        _computeButton.Text = L.T("Computing...");
        try
        {
            var result = await FileHasher.ComputeHashesAsync(path, selected, token);
            if (result.IsFailed)
            {
                MessageBox.Show(L.F("Failed to hash file: {0}", result.Errors[0].Message), L.T("Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            var hashes = result.Value;
            _md5Box.Text = hashes.TryGetValue(HashAlgorithmName.MD5, out var md5) ? md5 : "";
            _sha1Box.Text = hashes.TryGetValue(HashAlgorithmName.SHA1, out var sha1) ? sha1 : "";
            _sha256Box.Text = hashes.TryGetValue(HashAlgorithmName.SHA256, out var sha256) ? sha256 : "";
            _sha512Box.Text = hashes.TryGetValue(HashAlgorithmName.SHA512, out var sha512) ? sha512 : "";
            Verify();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.F("Failed to hash file: {0}", ex.Message), L.T("Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _computeButton.Enabled = true;
            _computeButton.Text = L.T("Compute Hashes");
        }
    }

    private void Verify()
    {
        var input = _verifyBox.Text.Trim().ToLowerInvariant();
        if (input.Length == 0)
        {
            _verifyLabel.Text = "";
            return;
        }

        (string Name, string Hash)[] hashes =
        [
            ("MD5", _md5Box.Text),
            ("SHA-1", _sha1Box.Text),
            ("SHA-256", _sha256Box.Text),
            ("SHA-512", _sha512Box.Text),
        ];

        foreach (var (name, hash) in hashes)
        {
            if (hash.Length > 0 && hash == input)
            {
                _verifyLabel.Text = L.F("MATCH: the file matches the provided {0} hash.", name);
                _verifyLabel.ForeColor = Theme.Success;
                return;
            }
        }

        _verifyLabel.Text = L.T("NO MATCH with any computed hash.");
        _verifyLabel.ForeColor = Theme.Danger;
    }
}
