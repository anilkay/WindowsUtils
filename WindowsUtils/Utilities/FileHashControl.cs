using System.Security.Cryptography;

namespace WindowsUtils.Utilities;

public class FileHashControl : UtilityControl
{
    private readonly TextBox _pathBox = new();
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

        _pathBox.PlaceholderText = "Select a file...";
        _pathBox.Width = 420;
        _pathBox.Margin = new Padding(4, 8, 4, 4);

        var browseButton = new Button { Text = "Browse...", AutoSize = true, Margin = new Padding(4, 6, 4, 4) };
        _computeButton.Text = "Compute Hashes";
        _computeButton.AutoSize = true;
        _computeButton.Margin = new Padding(4, 6, 4, 4);

        browseButton.Click += (_, _) => Browse();
        _computeButton.Click += async (_, _) => await ComputeAsync();

        topPanel.Controls.Add(_pathBox);
        topPanel.Controls.Add(browseButton);
        topPanel.Controls.Add(_computeButton);

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

        _verifyBox.PlaceholderText = "Paste a hash here to verify...";
        _verifyBox.TextChanged += (_, _) => Verify();
        AddRow(layout, 4, "Verify", _verifyBox);

        _verifyLabel.Dock = DockStyle.Fill;
        _verifyLabel.TextAlign = ContentAlignment.MiddleLeft;
        _verifyLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        layout.Controls.Add(_verifyLabel, 0, 5);
        layout.SetColumnSpan(_verifyLabel, 2);

        Controls.Add(layout);
        Controls.Add(topPanel);
    }

    private static TextBox MakeReadOnlyBox() => new() { ReadOnly = true, BackColor = SystemColors.Window };

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
        using var dialog = new OpenFileDialog { Filter = "All files (*.*)|*.*" };
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
            MessageBox.Show("Select an existing file first.", "File Hash", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _computeButton.Enabled = false;
        _computeButton.Text = "Computing...";
        try
        {
            var hashes = await Task.Run(() => ComputeHashes(path, token), token);
            _md5Box.Text = hashes[0];
            _sha1Box.Text = hashes[1];
            _sha256Box.Text = hashes[2];
            _sha512Box.Text = hashes[3];
            Verify();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to hash file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _computeButton.Enabled = true;
            _computeButton.Text = "Compute Hashes";
        }
    }

    private static string[] ComputeHashes(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        using var md5 = MD5.Create();
        using var sha1 = SHA1.Create();
        using var sha256 = SHA256.Create();
        using var sha512 = SHA512.Create();

        var algorithms = new HashAlgorithm[] { md5, sha1, sha256, sha512 };
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            foreach (var algorithm in algorithms)
                algorithm.TransformBlock(buffer, 0, read, null, 0);
        }
        foreach (var algorithm in algorithms)
            algorithm.TransformFinalBlock(buffer, 0, 0);

        return algorithms
            .Select(a => Convert.ToHexString(a.Hash!).ToLowerInvariant())
            .ToArray();
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
                _verifyLabel.Text = $"MATCH: the file matches the provided {name} hash.";
                _verifyLabel.ForeColor = Color.DarkGreen;
                return;
            }
        }

        _verifyLabel.Text = "NO MATCH with any computed hash.";
        _verifyLabel.ForeColor = Color.DarkRed;
    }
}
