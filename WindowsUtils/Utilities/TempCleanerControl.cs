using WindowsUtils.Core.IO;
using WindowsUtils.Localization;

namespace WindowsUtils.Utilities;

public class TempCleanerControl : UtilityControl
{
    private readonly DataGridView _grid;
    private readonly Button _scanButton = new() { Text = L.T("Scan"), AutoSize = true };
    private readonly Button _cleanButton = new() { Text = L.T("Clean Selected"), AutoSize = true, Enabled = false };
    private readonly Button _cancelButton = new() { Text = L.T("Cancel"), AutoSize = true, Enabled = false };
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
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Clean", HeaderText = L.T("Clean") });
        _grid.Columns.Add("Location", L.T("Location"));
        _grid.Columns.Add("Files", L.T("Files"));
        _grid.Columns.Add("Size", L.T("Total Size"));
        _grid.Columns.Add("Cleanable", L.T("Cleanable"));
        _grid.Columns.Add("Description", L.T("Description"));
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
            Text = L.F("Only these folders are touched. Files changed in the last {0:0} h or in use are kept. Deletes permanently.",
                TempCleaner.MinimumAge.TotalHours),
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
        _statusLabel.Text = L.T("Measuring temp and cache folders...");
        try
        {
            var locations = await Task.Run(TempCleaner.GetLocations, token);
            long total = 0, cleanable = 0;
            foreach (var location in locations)
            {
                var size = await Task.Run(() => TempCleaner.Measure(location, token), token);
                var row = _grid.Rows[_grid.Rows.Add(size.CleanableBytes > 0, L.V(location.Name), size.Files.ToString("N0"),
                    FormatBytes(size.Bytes), FormatBytes(size.CleanableBytes), L.V(location.Description))];
                row.Tag = size;
                row.Cells["Location"].ToolTipText = string.Join(Environment.NewLine, location.Roots.Take(10));
                if (size.CleanableBytes == 0)
                    row.DefaultCellStyle.ForeColor = Theme.SubtleText;
                total += size.Bytes;
                cleanable += size.CleanableBytes;
            }
            _statusLabel.Text = L.F("{0} in {1} locations; {2} can be cleaned.", FormatBytes(total), locations.Count, FormatBytes(cleanable));
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = L.T("Scan cancelled.");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = L.F("Error: {0}", ex.Message);
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
            _statusLabel.Text = L.T("Tick at least one location with something to clean.");
            return;
        }

        // The real folders are listed, so a surprising one (a TEMP pointing somewhere odd) shows up before anything is deleted.
        var lines = selected.Select(s => "  " + L.F("{0}: {1} ({2:N0} files)", L.V(s.Location.Name), FormatBytes(s.CleanableBytes), s.CleanableFiles) + "\n"
            + string.Join("\n", s.Location.Roots.Take(3).Select(r => $"      {r}"))
            + (s.Location.Roots.Count > 3 ? "\n      " + L.F("...and {0} more folders", s.Location.Roots.Count - 3) : ""));
        var message = L.F("Permanently delete {0} of temporary files?", FormatBytes(selected.Sum(s => s.CleanableBytes))) + "\n\n"
            + string.Join("\n", lines)
            + "\n\n" + L.T("The files are not moved to the Recycle Bin. Files in use are skipped.");
        var answer = MessageBox.Show(message, L.T("Clean Temp Files"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);
        long deleted = 0, freed = 0, skipped = 0;
        var refused = new List<string>();
        var cancelled = false;
        try
        {
            foreach (var size in selected)
            {
                _statusLabel.Text = L.F("Cleaning {0}...", L.V(size.Location.Name));
                var result = await Task.Run(() => TempCleaner.Clean(size.Location, token), token);
                if (result.IsFailed)
                {
                    refused.Add(result.Errors[0].Message);
                    continue;
                }
                deleted += result.Value.DeletedFiles;
                freed += result.Value.FreedBytes;
                skipped += result.Value.SkippedFiles;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true; // the location being cleaned when Cancel was pressed is not in the totals
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.F("Cleaning stopped: {0}", ex.Message), L.T("Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }

        await ScanAsync();
        if (refused.Count > 0)
            MessageBox.Show(L.T("Some locations were not cleaned:") + "\n\n" + string.Join("\n", refused), L.T("Clean Temp Files"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        if (cancelled)
        {
            _statusLabel.Text = L.T("Cleaning cancelled; some files may already be deleted (sizes above are current).") + " " + _statusLabel.Text;
            return;
        }
        _statusLabel.Text = (skipped > 0
                ? L.F("Freed {0} ({1:N0} files deleted, {2:N0} in use or protected were kept).", FormatBytes(freed), deleted, skipped)
                : L.F("Freed {0} ({1:N0} files deleted).", FormatBytes(freed), deleted))
            + " " + _statusLabel.Text;
    }

    private void SetBusy(bool busy)
    {
        _scanButton.Enabled = !busy;
        _cleanButton.Enabled = !busy;
        _cancelButton.Enabled = busy;
        _grid.Enabled = !busy;
    }
}
