using System.Data;
using WindowsUtils.Core.SystemInfo;

namespace WindowsUtils.Utilities;

public class EventLogControl : UtilityControl
{
    private static readonly (string Text, int Hours)[] Periods =
    [
        ("Last hour", 1),
        ("Last 24 hours", 24),
        ("Last 7 days", 24 * 7),
        ("Last 30 days", 24 * 30),
    ];

    private readonly DataGridView _grid;
    private readonly DataTable _table = new();
    private readonly ComboBox _periodBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly CheckBox _includeWarnings = new() { Text = "Include warnings", AutoSize = true, Checked = true };
    private readonly Button _refreshButton = new() { Text = "Refresh", AutoSize = true };
    private readonly Label _statusLabel = new();
    private readonly TextBox _messageBox = new()
    {
        Dock = DockStyle.Bottom,
        Height = 90,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
    };
    private CancellationTokenSource? _cts;

    public EventLogControl()
    {
        _table.Columns.Add("Level", typeof(string));
        _table.Columns.Add("Log", typeof(string));
        _table.Columns.Add("Source", typeof(string));
        _table.Columns.Add("Count", typeof(int));
        _table.Columns.Add("Latest", typeof(DateTime));
        _table.Columns.Add("EventIds", typeof(string));
        _table.Columns.Add("Message", typeof(string));

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4),
        };

        foreach (var (text, _) in Periods)
            _periodBox.Items.Add(text);
        _periodBox.SelectedIndex = 1;

        _refreshButton.Margin = new Padding(4, 6, 4, 4);
        _periodBox.Margin = new Padding(4, 8, 4, 4);
        _includeWarnings.Margin = new Padding(12, 11, 4, 4);
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(12, 12, 4, 4);

        _refreshButton.Click += async (_, _) => await LoadAsync();
        _periodBox.SelectionChangeCommitted += async (_, _) => await LoadAsync();
        _includeWarnings.CheckedChanged += async (_, _) => await LoadAsync();

        topPanel.Controls.Add(_refreshButton.AsAccent());
        topPanel.Controls.Add(_periodBox);
        topPanel.Controls.Add(_includeWarnings);
        topPanel.Controls.Add(_statusLabel);

        _grid = CreateGrid();
        _grid.AutoGenerateColumns = false;
        AddBoundColumn(_grid, "Level", "Level").AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        AddBoundColumn(_grid, "Log", "Log").AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        var source = AddBoundColumn(_grid, "Source", "Source");
        source.FillWeight = 30;
        source.MinimumWidth = 150;
        AddBoundColumn(_grid, "Count", "Count").AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        var latest = AddBoundColumn(_grid, "Latest", "Last Seen");
        latest.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        latest.DefaultCellStyle.Format = "g";
        var ids = AddBoundColumn(_grid, "EventIds", "Event IDs");
        ids.FillWeight = 14;
        ids.MinimumWidth = 80;
        AddBoundColumn(_grid, "Message", "Latest Message").FillWeight = 56;
        _grid.DataSource = _table;
        _grid.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex >= 0 && _grid.Columns[e.ColumnIndex].DataPropertyName == "Level"
                && e.Value is "Critical" or "Error")
            {
                e.CellStyle!.ForeColor = Theme.Danger;
            }
        };
        _grid.SelectionChanged += (_, _) =>
            _messageBox.Text = _grid.CurrentRow?.DataBoundItem is DataRowView row ? row["Message"] as string ?? "" : "";

        Controls.Add(_grid);
        Controls.Add(_messageBox);
        Controls.Add(topPanel);

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var hours = Periods[Math.Max(0, _periodBox.SelectedIndex)].Hours;
        var includeWarnings = _includeWarnings.Checked;
        _refreshButton.Enabled = false;
        _statusLabel.Text = "Reading the System and Application logs...";
        try
        {
            var result = await Task.Run(() => EventLogSummary.Get(hours, includeWarnings, cts.Token), cts.Token);
            if (result.IsFailed)
            {
                _statusLabel.Text = result.Errors[0].Message;
                return;
            }
            var groups = result.Value;
            _table.BeginLoadData();
            _table.Rows.Clear();
            foreach (var g in groups)
                _table.Rows.Add(g.Level, g.Log, g.Source, g.Count, g.Latest, g.EventIds, g.LatestMessage);
            _table.EndLoadData();
            _statusLabel.Text = $"{groups.Sum(g => g.Count)} event(s) from {groups.Count} source(s).";
        }
        catch (OperationCanceledException)
        {
            // A newer load replaced this one.
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
                _refreshButton.Enabled = true;
        }
    }
}
