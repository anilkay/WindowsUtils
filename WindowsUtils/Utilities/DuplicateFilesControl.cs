using System.Data;
using System.Diagnostics;
using WindowsUtils.Core.IO;

namespace WindowsUtils.Utilities;

public class DuplicateFilesControl : UtilityControl
{
    private const string AllDrivesText = "All fixed drives";

    private static readonly (string Text, long Bytes)[] MinSizes =
    [
        ("Any size", 1),
        ("1 KB or larger", 1024),
        ("1 MB or larger", 1024 * 1024),
        ("10 MB or larger", 10 * 1024 * 1024),
        ("100 MB or larger", 100 * 1024 * 1024),
    ];

    private readonly DataGridView _grid;
    private readonly DataTable _table = new();
    private readonly ComboBox _scopeBox = new();
    private readonly ComboBox _minSizeBox = new();
    private readonly Button _scanButton = new();
    private readonly Button _cancelButton = new();
    private readonly Label _statusLabel = new();
    private CancellationTokenSource? _cts;

    public DuplicateFilesControl()
    {
        _table.Columns.Add("Group", typeof(int));
        _table.Columns.Add("Name", typeof(string));
        _table.Columns.Add("Folder", typeof(string));
        _table.Columns.Add("Size", typeof(long));
        _table.Columns.Add("Modified", typeof(DateTime));
        _table.Columns.Add("FullPath", typeof(string));

        // ---- Top toolbar: scan scope + minimum size + actions ----
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
        _scopeBox.Width = 260;
        _scopeBox.Margin = new Padding(4, 8, 4, 4);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _scopeBox.Items.Add(profile);
        _scopeBox.Items.Add(AllDrivesText);
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                _scopeBox.Items.Add(drive.RootDirectory.FullName);
        }
        _scopeBox.Text = profile;

        var browseButton = new Button { Text = "Browse...", AutoSize = true, Margin = new Padding(4, 6, 4, 4) };

        _minSizeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _minSizeBox.Width = 140;
        _minSizeBox.Margin = new Padding(12, 8, 4, 4);
        foreach (var (text, _) in MinSizes)
            _minSizeBox.Items.Add(text);
        _minSizeBox.SelectedIndex = 2; // 1 MB: skips thousands of tiny config/icon files

        _scanButton.Text = "Find Duplicates";
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
        topPanel.Controls.Add(_minSizeBox);
        topPanel.Controls.Add(_scanButton);
        topPanel.Controls.Add(_cancelButton);

        // ---- Grid: one row per file, copies of the same file share a Group number ----
        _grid = CreateGrid();
        _grid.MultiSelect = true;
        _grid.AutoGenerateColumns = false;
        AddBoundColumn(_grid, "Group", "Group").FillWeight = 8;
        AddBoundColumn(_grid, "Name", "Name").FillWeight = 26;
        AddBoundColumn(_grid, "Folder", "Folder").FillWeight = 42;
        AddBoundColumn(_grid, "Size", "Size").FillWeight = 12;
        AddBoundColumn(_grid, "Modified", "Modified").FillWeight = 16;
        AddBoundColumn(_grid, "FullPath", "FullPath").Visible = false;
        MakeUnsortable(_grid); // header sorting would scatter the groups
        _grid.DataSource = _table;
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.CellStyle is null)
                return;
            // Shade every other group so copies read as one block.
            if (_grid.Rows[e.RowIndex].DataBoundItem is DataRowView row && (int)row["Group"] % 2 == 0)
                e.CellStyle.BackColor = Theme.Background;
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

        var selectCopiesButton = new Button { Text = "Select Copies (Keep Oldest)", AutoSize = true, Margin = new Padding(4, 6, 4, 4) };
        var openButton = new Button { Text = "Open in Explorer", AutoSize = true, Margin = new Padding(4, 6, 4, 4) };
        var deleteButton = new Button { Text = "Delete Selected", AutoSize = true, Margin = new Padding(4, 6, 4, 4) };
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(12, 12, 4, 4);

        selectCopiesButton.Click += (_, _) => SelectCopies();
        openButton.Click += (_, _) => OpenSelectedInExplorer();
        deleteButton.Click += (_, _) => DeleteSelected();

        bottomPanel.Controls.Add(selectCopiesButton);
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
        var minSize = MinSizes[Math.Max(0, _minSizeBox.SelectedIndex)].Bytes;

        _scanButton.Enabled = false;
        _cancelButton.Enabled = true;
        _table.Rows.Clear();

        var progress = new Progress<DuplicateScanProgress>(p => _statusLabel.Text = p.Phase switch
        {
            DuplicateScanPhase.Scanning => $"Scanning... {p.Done:N0} files checked",
            DuplicateScanPhase.Comparing => $"Comparing same-size files... {p.Done:N0} of {p.Total:N0}",
            _ => $"Hashing possible duplicates... {p.Done:N0} of {p.Total:N0}",
        });
        try
        {
            var (groups, scanned, errors) = await Task.Run(
                () => DuplicateFinder.FindDuplicates(roots, minSize, progress, token), token);

            _table.BeginLoadData();
            var number = 1;
            foreach (var group in groups)
            {
                foreach (var file in group.Files)
                {
                    _table.Rows.Add(
                        number,
                        Path.GetFileName(file.FullPath),
                        Path.GetDirectoryName(file.FullPath),
                        file.Size,
                        file.Modified,
                        file.FullPath);
                }
                number++;
            }
            _table.EndLoadData();

            _statusLabel.Text = groups.Count == 0
                ? $"Done. Scanned {scanned:N0} files, no duplicates found."
                : $"Done. Scanned {scanned:N0} files. {groups.Count:N0} groups of duplicates, "
                    + $"{FormatBytes(groups.Sum(g => g.WastedBytes))} could be freed.";
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

    /// <summary>Selects every copy except the oldest file in each group (files are listed oldest first).</summary>
    private void SelectCopies()
    {
        _grid.ClearSelection();
        int? previousGroup = null;
        var selected = 0;
        foreach (DataGridViewRow gridRow in _grid.Rows)
        {
            if (gridRow.DataBoundItem is not DataRowView row)
                continue;
            var group = (int)row["Group"];
            if (group == previousGroup)
            {
                gridRow.Selected = true;
                selected++;
            }
            previousGroup = group;
        }
        _statusLabel.Text = selected == 0
            ? "Nothing to select. Run a scan first."
            : $"Selected {selected:N0} copies. The oldest file in each group is kept.";
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

        // Warn when a selection would remove every copy of a file.
        var groupSizes = _table.Rows.Cast<DataRow>()
            .GroupBy(r => (int)r["Group"])
            .ToDictionary(g => g.Key, g => g.Count());
        var allCopiesGroups = rows
            .GroupBy(r => (int)r["Group"])
            .Count(g => g.Count() == groupSizes[g.Key]);

        var totalSize = rows.Sum(r => (long)r["Size"]);
        var message = $"Delete {rows.Count} file(s) ({FormatBytes(totalSize)})? They will be moved to the Recycle Bin.\n\n"
            + "Files that cannot be recycled (too large, or on a network or USB drive) will ask before being permanently deleted.";
        if (allCopiesGroups > 0)
            message = $"Warning: for {allCopiesGroups} group(s) you selected every copy, so no copy of those files will be left.\n\n" + message;

        var answer = MessageBox.Show(message, "Delete Files", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
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

        RemoveResolvedGroups();
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

    /// <summary>Drops groups that have a single file left: it is no longer a duplicate.</summary>
    private void RemoveResolvedGroups()
    {
        var remaining = _table.Rows.Cast<DataRow>().Where(r => r.RowState != DataRowState.Deleted).ToList();
        foreach (var group in remaining.GroupBy(r => (int)r["Group"]).Where(g => g.Count() < 2))
        {
            foreach (var row in group)
                row.Delete();
        }
        _table.AcceptChanges();
    }
}
