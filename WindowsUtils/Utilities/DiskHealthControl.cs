using WindowsUtils.Core.SystemInfo;

namespace WindowsUtils.Utilities;

// One column per physical disk, one row per property: there are rarely more than a few disks,
// and eleven properties side by side do not fit the window.
public class DiskHealthControl : UtilityControl
{
    private static readonly string[] Properties =
    [
        "Name",
        "Type",
        "Size",
        "Health",
        "Operational status",
        "SMART failure prediction",
        "Temperature",
        "Wear",
        "Power-on hours",
        "Uncorrected read errors",
        "Uncorrected write errors",
        "Start/stop cycles",
    ];

    private readonly DataGridView _grid;
    private readonly Button _refreshButton = new() { Text = "Refresh", AutoSize = true };
    private readonly Label _statusLabel = new();

    public DiskHealthControl()
    {
        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4),
        };

        _refreshButton.Margin = new Padding(4, 6, 4, 4);
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(12, 12, 4, 4);
        _refreshButton.Click += async (_, _) => await LoadAsync();

        topPanel.Controls.Add(_refreshButton.AsAccent());
        topPanel.Controls.Add(_statusLabel);

        _grid = CreateGrid();
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;

        var note = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Text = "Read-only. Temperature, wear and error counters need Administrator and a drive that reports them.",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
        };

        Controls.Add(_grid);
        Controls.Add(note);
        Controls.Add(topPanel);

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _refreshButton.Enabled = false;
        _statusLabel.Text = "Reading disk health...";
        try
        {
            var result = await Task.Run(DiskHealth.Get);
            if (result.IsFailed)
            {
                _statusLabel.Text = result.Errors[0].Message;
                return;
            }

            var disks = result.Value;
            Fill(disks);
            var attention = disks.Count(d => d.NeedsAttention);
            _statusLabel.Text = attention == 0
                ? $"{disks.Count} disk(s), all healthy."
                : $"{disks.Count} disk(s), {attention} need attention.";
        }
        finally
        {
            _refreshButton.Enabled = true;
        }
    }

    private void Fill(IReadOnlyList<DiskHealthInfo> disks)
    {
        _grid.Rows.Clear();
        _grid.Columns.Clear();
        var property = new DataGridViewTextBoxColumn { Name = "Property", HeaderText = "", FillWeight = 30 };
        property.DefaultCellStyle.ForeColor = Theme.SubtleText;
        _grid.Columns.Add(property);
        foreach (var disk in disks)
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = $"Disk {disk.DeviceId}", FillWeight = 70f / Math.Max(1, disks.Count) });
        MakeUnsortable(_grid);

        var values = disks.Select(Values).ToList();
        for (var row = 0; row < Properties.Length; row++)
        {
            var cells = new object[disks.Count + 1];
            cells[0] = Properties[row];
            for (var d = 0; d < disks.Count; d++)
                cells[d + 1] = values[d][row];
            _grid.Rows.Add(cells);
        }

        // Mark the problem cells, not the whole disk.
        for (var d = 0; d < disks.Count; d++)
        {
            var disk = disks[d];
            if (disk.Health != "Healthy")
                _grid.Rows[3].Cells[d + 1].Style.ForeColor = Theme.Danger;
            if (disk.PredictsFailure == true)
                _grid.Rows[5].Cells[d + 1].Style.ForeColor = Theme.Danger;
            if (disk.WearPercent >= 90)
                _grid.Rows[7].Cells[d + 1].Style.ForeColor = Theme.Danger;
            if (disk.ReadErrorsUncorrected > 0)
                _grid.Rows[9].Cells[d + 1].Style.ForeColor = Theme.Danger;
            if (disk.WriteErrorsUncorrected > 0)
                _grid.Rows[10].Cells[d + 1].Style.ForeColor = Theme.Danger;
        }
    }

    // Same order as Properties.
    private static string[] Values(DiskHealthInfo d) =>
    [
        d.Name,
        $"{d.MediaType}, {d.BusType}",
        FormatBytes(d.SizeBytes),
        d.Health,
        d.OperationalStatus,
        d.PredictsFailure switch { true => "Failure predicted", false => "No failure predicted", null => NotReported },
        d.TemperatureC is { } t ? $"{t} °C" + (d.TemperatureMaxC is { } max ? $" (max {max} °C)" : "") : NotReported,
        d.WearPercent is { } w ? $"{w}%" : NotReported,
        d.PowerOnHours is { } h ? $"{h:N0}" : NotReported,
        d.ReadErrorsUncorrected?.ToString("N0") ?? NotReported,
        d.WriteErrorsUncorrected?.ToString("N0") ?? NotReported,
        d.StartStopCycles?.ToString("N0") ?? NotReported,
    ];

    private const string NotReported = "Not reported";
}
