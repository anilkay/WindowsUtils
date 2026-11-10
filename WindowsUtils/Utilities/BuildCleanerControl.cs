using System.Diagnostics;
using WindowsUtils.Core.IO;

namespace WindowsUtils.Utilities;

public class BuildCleanerControl : UtilityControl
{
    private readonly DataGridView _grid;
    private readonly TextBox _pathBox = new();
    private readonly Button _scanButton = new() { Text = "Scan", AutoSize = true };
    private readonly Button _cleanButton = new() { Text = "Delete Selected", AutoSize = true, Enabled = false };
    private readonly Button _cancelButton = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly Label _statusLabel = new();
    private CancellationTokenSource? _cts;

    public BuildCleanerControl()
    {
        // ---- Top toolbar: repository folder + scan ----
        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4),
        };

        _pathBox.Width = 420;
        _pathBox.Margin = new Padding(4, 8, 4, 4);
        _pathBox.Text = BuildArtifactCleaner.DefaultRoot;
        var browseButton = new Button { Text = "Browse...", AutoSize = true, Margin = new Padding(4, 6, 4, 4) };
        _scanButton.Margin = new Padding(4, 6, 4, 4);
        _cancelButton.Margin = new Padding(4, 6, 4, 4);
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(12, 12, 4, 4);

        browseButton.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Folder that holds your source repositories" };
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                _pathBox.Text = dialog.SelectedPath;
                _ = ScanAsync();
            }
        };
        _scanButton.Click += async (_, _) => await ScanAsync();
        _cancelButton.Click += (_, _) => _cts?.Cancel();
        _pathBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                _ = ScanAsync();
            }
        };

        topPanel.Controls.Add(new Label { Text = "Repos:", AutoSize = true, Margin = new Padding(4, 12, 4, 4) });
        topPanel.Controls.Add(_pathBox);
        topPanel.Controls.Add(browseButton);
        topPanel.Controls.Add(_scanButton.AsAccent());
        topPanel.Controls.Add(_cancelButton);

        // ---- Grid: one row per bin/obj folder, ticked by default ----
        _grid = CreateGrid();
        _grid.ReadOnly = false;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Clean", HeaderText = "Clean" });
        _grid.Columns.Add("Project", "Project");
        _grid.Columns.Add("Folder", "Folder");
        _grid.Columns.Add("Files", "Files");
        _grid.Columns.Add("Size", "Size");
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.ReadOnly = column.Name != "Clean";
            if (column.Name != "Project")
                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        }
        MakeUnsortable(_grid);
        // Commit the checkbox right away so the Delete button sees the new state.
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
                OpenSelectedInExplorer();
        };

        // ---- Bottom toolbar: open / delete + status ----
        var bottomPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4),
        };
        var openButton = new Button { Text = "Open in Explorer", AutoSize = true, Margin = new Padding(4, 6, 4, 4) };
        _cleanButton.Margin = new Padding(4, 6, 4, 4);
        _statusLabel.Margin = new Padding(12, 12, 4, 4);
        openButton.Click += (_, _) => OpenSelectedInExplorer();
        _cleanButton.Click += async (_, _) => await DeleteAsync();
        bottomPanel.Controls.Add(openButton);
        bottomPanel.Controls.Add(_cleanButton.AsAccent());
        bottomPanel.Controls.Add(_statusLabel);

        var note = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Text = "Only bin and obj folders next to a project file are deleted. Permanent (not the Recycle Bin); "
                + "the next build recreates them.",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
        };

        Controls.Add(_grid);
        Controls.Add(bottomPanel);
        Controls.Add(note);
        Controls.Add(topPanel);

        if (Directory.Exists(_pathBox.Text))
            _ = ScanAsync();
        else
            _statusLabel.Text = "Choose your source repositories folder and press Scan.";
    }

    private async Task ScanAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);
        _grid.Rows.Clear();
        _statusLabel.Text = "Looking for .NET projects...";

        try
        {
            var progress = new Progress<BuildScanProgress>(p =>
                _statusLabel.Text = $"Scanning... {p.ProjectsScanned:N0} projects");
            var result = await Task.Run(
                () => BuildArtifactCleaner.Scan(_pathBox.Text.Trim(), progress, token), token);
            if (result.IsFailed)
            {
                _statusLabel.Text = result.Errors[0].Message;
                return;
            }

            var scan = result.Value;
            foreach (var artifact in scan.Artifacts)
            {
                var project = Path.GetDirectoryName(artifact.ProjectFile) ?? artifact.Folder;
                var row = _grid.Rows[_grid.Rows.Add(true,
                    Path.GetRelativePath(scan.Root, project),
                    Path.GetFileName(artifact.Folder),
                    artifact.Files.ToString("N0"),
                    FormatBytes(artifact.Bytes))];
                row.Tag = artifact;
                row.Cells["Project"].ToolTipText = artifact.ProjectFile;
                row.Cells["Folder"].ToolTipText = artifact.Folder;
            }

            _statusLabel.Text = scan.Artifacts.Count == 0
                ? $"No bin or obj folders found in {scan.ProjectsScanned:N0} projects."
                : $"{scan.Artifacts.Count:N0} folders in {scan.ProjectsScanned:N0} projects, {FormatBytes(scan.Bytes)} total. "
                    + "Untick anything to keep, then Delete Selected.";
            if (scan.Errors.Count > 0)
                _statusLabel.Text += $" Could not read some of the folder: {scan.Errors[0].Message}";
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
            SetBusy(false);
        }
    }

    private async Task DeleteAsync()
    {
        var selected = _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells["Clean"].Value is true && r.Tag is BuildArtifact)
            .Select(r => (BuildArtifact)r.Tag!)
            .ToList();
        if (selected.Count == 0)
        {
            _statusLabel.Text = "Tick at least one bin or obj folder to delete.";
            return;
        }

        var total = selected.Sum(a => a.Bytes);
        var message = $"Permanently delete {selected.Count:N0} build output folder(s) ({FormatBytes(total)})?\n\n"
            + string.Join("\n", selected.Take(8).Select(a => $"  {a.Folder}"))
            + (selected.Count > 8 ? $"\n  ...and {selected.Count - 8} more" : "")
            + "\n\nThey are not moved to the Recycle Bin. The next build recreates them.";
        var answer = MessageBox.Show(message, "Delete Build Outputs", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);
        long folders = 0, files = 0, freed = 0, failed = 0;
        var refused = new List<string>();
        var cancelled = false;
        try
        {
            foreach (var artifact in selected)
            {
                _statusLabel.Text = $"Deleting {artifact.Folder}...";
                var result = await Task.Run(() => BuildArtifactCleaner.Clean(artifact, token), token);
                if (result.IsFailed)
                {
                    refused.Add(result.Errors[0].Message);
                    continue;
                }
                folders += result.Value.FoldersDeleted;
                files += result.Value.FilesDeleted;
                freed += result.Value.FreedBytes;
                failed += result.Value.FailedEntries;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true; // folders deleted before Cancel are already gone
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Deleting stopped: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }

        var summary = $"Deleted {folders:N0} folder(s), freed {FormatBytes(freed)} ({files:N0} files"
            + (failed > 0 ? $", {failed:N0} locked or protected entries kept" : "") + ").";
        await ScanAsync();
        _statusLabel.Text = summary + " " + _statusLabel.Text;

        if (refused.Count > 0)
            MessageBox.Show("Some folders were not deleted:\n\n" + string.Join("\n", refused.Take(10)),
                "Delete Build Outputs", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        if (cancelled)
            _statusLabel.Text = "Deleting cancelled; some folders may already be deleted. " + _statusLabel.Text;
    }

    private void OpenSelectedInExplorer()
    {
        if (_grid.SelectedRows.Count == 0 || _grid.SelectedRows[0].Tag is not BuildArtifact artifact)
        {
            _statusLabel.Text = "Select a row first.";
            return;
        }
        try
        {
            if (!Directory.Exists(artifact.Folder))
            {
                _statusLabel.Text = "Folder no longer exists.";
                return;
            }
            Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                $"\"{artifact.Folder}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open Explorer: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetBusy(bool busy)
    {
        _scanButton.Enabled = !busy;
        _cancelButton.Enabled = busy;
        _grid.Enabled = !busy;
        _cleanButton.Enabled = !busy && _grid.Rows.Count > 0;
    }
}
