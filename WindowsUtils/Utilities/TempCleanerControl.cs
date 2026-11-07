using WindowsUtils.Core.IO;

namespace WindowsUtils.Utilities;

public class TempCleanerControl : UtilityControl
{
    private readonly DataGridView _grid;
    private readonly Button _scanButton = new() { Text = "Scan", AutoSize = true };
    private readonly Button _cleanButton = new() { Text = "Clean Selected", AutoSize = true, Enabled = false };
    private readonly Button _cancelButton = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly Label _statusLabel = new();
    private CancellationTokenSource? _cts;

    public TempCleanerControl()
    {
        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4),
        };

        _scanButton.Margin = new Padding(4, 6, 4, 4);
        _cleanButton.Margin = new Padding(4, 6, 4, 4);
        _cancelButton.Margin = new Padding(4, 6, 4, 4);
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(12, 12, 4, 4);

        _scanButton.Click += async (_, _) => await ScanAsync();
        _cleanButton.Click += async (_, _) => await CleanAsync();
        _cancelButton.Click += (_, _) => _cts?.Cancel();

        topPanel.Controls.Add(_scanButton);
        topPanel.Controls.Add(_cleanButton.AsAccent());
        topPanel.Controls.Add(_cancelButton);
        topPanel.Controls.Add(_statusLabel);

        // Unbound grid: only the checkbox column is editable.
        _grid = CreateGrid();
        _grid.ReadOnly = false;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Clean", HeaderText = "Clean" });
        _grid.Columns.Add("Location", "Location");
        _grid.Columns.Add("Files", "Files");
        _grid.Columns.Add("Size", "Total Size");
        _grid.Columns.Add("Cleanable", "Cleanable");
        _grid.Columns.Add("Description", "Description");
        // Everything but Description is sized to its content so numbers are never cut off.
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.ReadOnly = column.Name != "Clean";
            if (column.Name != "Description")
                column.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        }
        MakeUnsortable(_grid);
        // Commit the checkbox right away so the Clean button sees the new state.
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        var note = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Text = $"Only these folders are touched. Files changed in the last {TempCleaner.MinimumAge.TotalHours:0} h or in use are kept. "
                + "Deletes permanently.",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
        };

        Controls.Add(_grid);
        Controls.Add(note);
        Controls.Add(topPanel);

        _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);
        _grid.Rows.Clear();
        _statusLabel.Text = "Measuring temp and cache folders...";
        try
        {
            var locations = await Task.Run(TempCleaner.GetLocations, token);
            long total = 0, cleanable = 0;
            foreach (var location in locations)
            {
                var size = await Task.Run(() => TempCleaner.Measure(location, token), token);
                var row = _grid.Rows[_grid.Rows.Add(size.CleanableBytes > 0, location.Name, size.Files.ToString("N0"),
                    FormatBytes(size.Bytes), FormatBytes(size.CleanableBytes), location.Description)];
                row.Tag = size;
                row.Cells["Location"].ToolTipText = string.Join(Environment.NewLine, location.Roots.Take(10));
                if (size.CleanableBytes == 0)
                    row.DefaultCellStyle.ForeColor = Theme.SubtleText;
                total += size.Bytes;
                cleanable += size.CleanableBytes;
            }
            _statusLabel.Text = $"{FormatBytes(total)} in {locations.Count} locations; {FormatBytes(cleanable)} can be cleaned.";
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

    private async Task CleanAsync()
    {
        var selected = _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells["Clean"].Value is true && r.Tag is CleanupSize { CleanableBytes: > 0 })
            .Select(r => (CleanupSize)r.Tag!)
            .ToList();
        if (selected.Count == 0)
        {
            _statusLabel.Text = "Tick at least one location with something to clean.";
            return;
        }

        var lines = selected.Select(s => $"  {s.Location.Name}: {FormatBytes(s.CleanableBytes)} ({s.CleanableFiles:N0} files)");
        var message = $"Permanently delete {FormatBytes(selected.Sum(s => s.CleanableBytes))} of temporary files?\n\n"
            + string.Join("\n", lines)
            + "\n\nThe files are not moved to the Recycle Bin. Files in use are skipped.";
        var answer = MessageBox.Show(message, "Clean Temp Files", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);
        long deleted = 0, freed = 0, skipped = 0;
        var cancelled = false;
        try
        {
            foreach (var size in selected)
            {
                _statusLabel.Text = $"Cleaning {size.Location.Name}...";
                var result = await Task.Run(() => TempCleaner.Clean(size.Location, token), token);
                deleted += result.DeletedFiles;
                freed += result.FreedBytes;
                skipped += result.SkippedFiles;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true; // the location being cleaned when Cancel was pressed is not in the totals
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cleaning stopped: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }

        await ScanAsync();
        if (cancelled)
        {
            _statusLabel.Text = "Cleaning cancelled; some files may already be deleted (sizes above are current). " + _statusLabel.Text;
            return;
        }
        _statusLabel.Text = $"Freed {FormatBytes(freed)} ({deleted:N0} files deleted"
            + (skipped > 0 ? $", {skipped:N0} in use or protected were kept" : "") + "). " + _statusLabel.Text;
    }

    private void SetBusy(bool busy)
    {
        _scanButton.Enabled = !busy;
        _cleanButton.Enabled = !busy;
        _cancelButton.Enabled = busy;
        _grid.Enabled = !busy;
    }
}
