using WindowsUtils.Core.SystemInfo;

namespace WindowsUtils.Utilities;

public class BatteryControl : UtilityControl
{
    private readonly DataGridView _grid;
    private readonly Button _refreshButton = new() { Text = "Refresh", AutoSize = true };
    private readonly Label _statusLabel = new();

    public BatteryControl()
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
        _grid.Columns.Add("Property", "Property");
        _grid.Columns.Add("Value", "Value");
        _grid.Columns["Property"]!.FillWeight = 30;
        _grid.Columns["Value"]!.FillWeight = 70;
        MakeUnsortable(_grid);

        var note = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Text = "Read-only. Health = full-charge capacity as a share of the design capacity, as reported by the battery driver.",
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
        _statusLabel.Text = "Reading battery and power status...";
        try
        {
            var report = await Task.Run(PowerInfo.Get);
            _grid.Rows.Clear();
            AddRow("Power Source", report.PowerSource);
            AddRow("Power Plan", report.PowerPlan ?? "Unknown");
            AddRow("Battery Saver", report.BatterySaverOn ? "On" : "Off", subtle: !report.BatterySaverOn);

            if (!report.HasBattery)
            {
                AddRow("Battery", "No battery detected (desktop, server or virtual machine).", subtle: true);
            }
            else
            {
                AddRow("Charge", report.ChargePercent is int p ? $"{p}%" : "Unknown");
                AddRow("State", report.ChargeState);
                AddRow("Time Remaining", report.TimeRemaining is TimeSpan t ? $"{(int)t.TotalHours}h {t.Minutes}m" : "Not available",
                    subtle: report.TimeRemaining is null);
                foreach (var battery in report.Batteries)
                    AddBatteryRows(battery, report.Batteries.Count > 1);
            }
            _statusLabel.Text = report.HasBattery
                ? $"{report.ChargePercent?.ToString() ?? "?"}% · {report.ChargeState}"
                : "No battery.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _refreshButton.Enabled = true;
        }
    }

    private void AddBatteryRows(BatteryDetails battery, bool prefixName)
    {
        var prefix = prefixName ? $"{battery.Name}: " : "";
        AddRow($"{prefix}Battery", battery.Name + (battery.Manufacturer is null ? "" : $" ({battery.Manufacturer})"));
        if (battery.Chemistry is not null)
            AddRow($"{prefix}Chemistry", battery.Chemistry);
        AddRow($"{prefix}Design Capacity", FormatCapacity(battery.DesignCapacity));
        AddRow($"{prefix}Full Charge Capacity", FormatCapacity(battery.FullChargeCapacity));
        if (battery.HealthPercent is double health)
        {
            var row = AddRow($"{prefix}Health", $"{health:0.#}%" + (health < 60 ? " (worn, consider replacing)" : ""));
            if (health < 60)
                row.Cells[1].Style.ForeColor = Theme.Danger;
        }
        else
        {
            AddRow($"{prefix}Health", "Not reported by the driver", subtle: true);
        }
        AddRow($"{prefix}Cycle Count", battery.CycleCount?.ToString() ?? "Not reported", subtle: battery.CycleCount is null);
        if (battery.RemainingCapacity is long remaining)
            AddRow($"{prefix}Remaining Capacity", FormatCapacity(remaining));
        if (battery.RateMilliwatts is long rate)
            AddRow($"{prefix}{battery.State} Rate", $"{rate / 1000.0:0.0} W");
    }

    private static string FormatCapacity(long? milliwattHours) =>
        milliwattHours is long value ? $"{value:N0} mWh" : "Not reported";

    private DataGridViewRow AddRow(string name, string value, bool subtle = false)
    {
        var row = _grid.Rows[_grid.Rows.Add(name, value)];
        if (subtle)
            row.Cells[1].Style.ForeColor = Theme.SubtleText;
        return row;
    }
}
