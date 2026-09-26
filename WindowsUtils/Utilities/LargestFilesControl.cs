using System.Data;
using System.Diagnostics;
using WindowsUtils.Core.IO;

namespace WindowsUtils.Utilities;

public class LargestFilesControl : UtilityControl
{
    private const string AllDrivesText = "All fixed drives";
    private const int MaxResults = 100;

    private readonly DataGridView _grid;
    private readonly DataTable _table = new();
    private readonly ComboBox _scopeBox = new();
    private readonly Button _scanButton = new();
    private readonly Button _cancelButton = new();
    private readonly Label _statusLabel = new();
    private CancellationTokenSource? _cts;

    public LargestFilesControl()
    {
        _table.Columns.Add("Rank", typeof(int));
        _table.Columns.Add("Name", typeof(string));
        _table.Columns.Add("Folder", typeof(string));
        _table.Columns.Add("Size", typeof(long));
        _table.Columns.Add("Modified", typeof(DateTime));
        _table.Columns.Add("FullPath", typeof(string));

        // ---- Top toolbar: scan scope + actions ----
        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4),
        };

        topPanel.Controls.Add(new Label { Text = "Scan:", AutoSize = true, Margin = new Padding(4, 12, 4, 4) });

        _scopeBox.DropDownStyle = ComboBoxStyle.DropDown;
        _scopeBox.Width = 280;
        _scopeBox.Margin = new Padding(4, 8, 4, 4);
        _scopeBox.Items.Add(AllDrivesText);
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                _scopeBox.Items.Add(drive.RootDirectory.FullName);
        }
        _scopeBox.Text = AllDrivesText;

        var browseButton = new Button { Text = "Browse...", AutoSize = true, Margin = new Padding(4, 6, 4, 4) };
        _scanButton.Text = "Scan";
        _scanButton.AutoSize = true;
        _scanButton.AsAccent();
        _scanButton.Margin = new Padding(4, 6, 4, 4);
        _cancelButton.Text = "Cancel";
        _cancelButton.AutoSize = true;
        _cancelButton.Margin = new Padding(4, 6, 4, 4);
        _cancelButton.Enabled = false;

        browseButton.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog() == DialogResult.OK)
                _scopeBox.Text = dialog.SelectedPath;
        };
        _scanButton.Click += async (_, _) => await ScanAsync();
        _cancelButton.Click += (_, _) => _cts?.Cancel();

        topPanel.Controls.Add(_scopeBox);
        topPanel.Controls.Add(browseButton);
        topPanel.Controls.Add(_scanButton);
        topPanel.Controls.Add(_cancelButton);

        // ---- Grid ----
        _grid = CreateGrid();
        _grid.MultiSelect = true;
        _grid.AutoGenerateColumns = false;
        AddBoundColumn(_grid, "Rank", "Rank").FillWeight = 8;
        AddBoundColumn(_grid, "Name", "Name").FillWeight = 26;
        AddBoundColumn(_grid, "Folder", "Folder").FillWeight = 42;
        AddBoundColumn(_grid, "Size", "Size").FillWeight = 12;
        AddBoundColumn(_grid, "Modified", "Modified").FillWeight = 16;
        AddBoundColumn(_grid, "FullPath", "FullPath").Visible = false;
        _grid.DataSource = _table;
        _grid.CellFormatting += (_, e) =>
        {
            if (e.Value is long bytes && _grid.Columns[e.ColumnIndex].DataPropertyName == "Size")
            {
                e.Value = FormatBytes(bytes);
                e.FormattingApplied = true;
            }
        };
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
                OpenSelectedInExplorer();
        };

        // ---- Bottom toolbar: file actions + status ----
        var bottomPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4),
        };

        var openButton = new Button { Text = "Open in Explorer", AutoSize = true, Margin = new Padding(4, 6, 4, 4) };
        var deleteButton = new Button { Text = "Delete Selected", AutoSize = true, Margin = new Padding(4, 6, 4, 4) };
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(12, 12, 4, 4);

        openButton.Click += (_, _) => OpenSelectedInExplorer();
        deleteButton.Click += (_, _) => DeleteSelected();

        bottomPanel.Controls.Add(openButton);
        bottomPanel.Controls.Add(deleteButton);
        bottomPanel.Controls.Add(_statusLabel);

        Controls.Add(_grid);
        Controls.Add(bottomPanel);
        Controls.Add(topPanel);
    }

    private List<string> GetScanRoots()
    {
        var text = _scopeBox.Text.Trim();
        if (text.Equals(AllDrivesText, StringComparison.OrdinalIgnoreCase))
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        return Directory.Exists(text) ? [text] : [];
    }

    private async Task ScanAsync()
    {
        var roots = GetScanRoots();
        if (roots.Count == 0)
        {
            _statusLabel.Text = "Pick a valid drive or folder to scan.";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _scanButton.Enabled = false;
        _cancelButton.Enabled = true;
        _table.Rows.Clear();

        var progress = new Progress<ScanProgress>(p => _statusLabel.Text = $"Scanning... {p.Scanned:N0} files checked");
        try
        {
            var (files, scanned, errors) = await Task.Run(
                () => FileScanner.FindLargestFiles(roots, MaxResults, progress, token), token);

            var rank = 1;
            foreach (var file in files)
            {
                _table.Rows.Add(
                    rank++,
                    Path.GetFileName(file.FullPath),
                    Path.GetDirectoryName(file.FullPath),
                    file.Size,
                    file.Modified,
                    file.FullPath);
            }
            _table.DefaultView.Sort = "Size DESC";

            _statusLabel.Text = files.Count == 0
                ? $"Done. Scanned {scanned:N0} files, nothing found."
                : $"Done. Scanned {scanned:N0} files. Top {files.Count} files total {FormatBytes(files.Sum(f => f.Size))}. Double-click a row to open it in Explorer.";
            if (errors.Count > 0)
                _statusLabel.Text += $" Skipped {string.Join(", ", errors.Select(e => $"{e.Root} ({e.Message})"))}";
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _scanButton.Enabled = true;
            _cancelButton.Enabled = false;
        }
    }

    private void OpenSelectedInExplorer()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            _statusLabel.Text = "Select a file first.";
            return;
        }
        if (_grid.SelectedRows[0].DataBoundItem is not DataRowView row)
            return;

        var path = (string)row["FullPath"];
        try
        {
            if (File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
                _statusLabel.Text = "File no longer exists.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open Explorer: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DeleteSelected()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            _statusLabel.Text = "Select one or more files first.";
            return;
        }

        var rows = _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(r => r.DataBoundItem)
            .OfType<DataRowView>()
            .ToList();

        var totalSize = rows.Sum(r => (long)r["Size"]);
        var answer = MessageBox.Show(
            $"Delete {rows.Count} file(s) ({FormatBytes(totalSize)})? They will be moved to the Recycle Bin.\n\n"
                + "Files that cannot be recycled (too large, or on a network or USB drive) will ask before being permanently deleted.",
            "Delete Files",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
            return;

        var deleted = 0;
        var kept = 0;
        var failures = new List<string>();
        var owner = FindForm()?.Handle ?? Handle;
        foreach (var row in rows)
        {
            var path = (string)row["FullPath"];
            try
            {
                if (!RecycleBin.SendToRecycleBin(path, owner))
                {
                    kept++; // could not be recycled and the user declined permanent deletion
                    continue;
                }
                row.Row.Delete();
                deleted++;
            }
            catch (Exception ex)
            {
                failures.Add($"{path}: {ex.Message}");
            }
        }

        Renumber();
        _statusLabel.Text = $"Deleted {deleted} file(s)."
            + (kept > 0 ? $" Kept {kept} that could not be recycled." : "")
            + (failures.Count > 0 ? $" {failures.Count} failed." : "");

        if (failures.Count > 0)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, failures.Take(5)),
                "Some files could not be deleted",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void Renumber()
    {
        var rank = 1;
        foreach (DataRowView row in _table.DefaultView)
            row["Rank"] = rank++;
    }
}
