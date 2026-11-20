using System.Data;
using WindowsUtils.Core.Net;

namespace WindowsUtils.Utilities;

public class OpenPortsControl : UtilityControl
{
    private readonly DataGridView _grid;
    private readonly DataTable _table = new();
    private readonly TextBox _filterBox = new() { PlaceholderText = "Filter by process, port or address...", Width = 260 };
    private readonly CheckBox _listeningOnly = new() { Text = "Listening only", AutoSize = true, Checked = true };
    private readonly Button _refreshButton = new() { Text = "Refresh", AutoSize = true };
    private readonly Label _statusLabel = new();
    private IReadOnlyList<PortEntry> _ports = [];

    public OpenPortsControl()
    {
        // Typed port columns, so clicking the header sorts numerically.
        _table.Columns.Add("Protocol", typeof(string));
        _table.Columns.Add("LocalAddress", typeof(string));
        _table.Columns.Add("LocalPort", typeof(int));
        _table.Columns.Add("RemoteAddress", typeof(string));
        _table.Columns.Add("RemotePort", typeof(int));
        _table.Columns.Add("State", typeof(string));
        _table.Columns.Add("ProcessId", typeof(int));
        _table.Columns.Add("ProcessName", typeof(string));

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4),
        };

        _refreshButton.Margin = new Padding(4, 6, 4, 4);
        _filterBox.Margin = new Padding(4, 8, 4, 4);
        _listeningOnly.Margin = new Padding(12, 11, 4, 4);
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(12, 12, 4, 4);

        _refreshButton.Click += async (_, _) => await LoadAsync();
        _filterBox.TextChanged += (_, _) => ApplyFilter();
        _listeningOnly.CheckedChanged += (_, _) => Fill();

        topPanel.Controls.Add(_refreshButton.AsAccent());
        topPanel.Controls.Add(_filterBox);
        topPanel.Controls.Add(_listeningOnly);
        topPanel.Controls.Add(_statusLabel);

        _grid = CreateGrid();
        _grid.AutoGenerateColumns = false;
        AddBoundColumn(_grid, "Protocol", "Protocol").AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        var localAddress = AddBoundColumn(_grid, "LocalAddress", "Local Address");
        localAddress.FillWeight = 26;
        localAddress.MinimumWidth = 120;
        AddBoundColumn(_grid, "LocalPort", "Local Port").AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        var remoteAddress = AddBoundColumn(_grid, "RemoteAddress", "Remote Address");
        remoteAddress.FillWeight = 26;
        remoteAddress.MinimumWidth = 120;
        AddBoundColumn(_grid, "RemotePort", "Remote Port").AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        AddBoundColumn(_grid, "State", "State").AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        AddBoundColumn(_grid, "ProcessId", "PID").AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        var process = AddBoundColumn(_grid, "ProcessName", "Process");
        process.FillWeight = 22;
        process.MinimumWidth = 110;
        _grid.DataSource = _table;

        var note = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Text = "Read-only (like netstat -ano). Process names of other users' services need Administrator.",
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
        _statusLabel.Text = "Reading connection tables...";
        try
        {
            var result = await Task.Run(OpenPorts.Get);
            if (result.IsFailed)
            {
                _statusLabel.Text = result.Errors[0].Message;
                return;
            }
            _ports = result.Value;
            Fill();
        }
        finally
        {
            _refreshButton.Enabled = true;
        }
    }

    private void Fill()
    {
        var rows = _listeningOnly.Checked ? _ports.Where(p => p.IsListening).ToList() : _ports.ToList();
        _table.BeginLoadData();
        _table.Rows.Clear();
        foreach (var p in rows)
        {
            _table.Rows.Add(p.Protocol, p.LocalAddress, p.LocalPort, p.RemoteAddress,
                p.RemotePort is int remote ? remote : DBNull.Value, p.State, p.ProcessId, p.ProcessName);
        }
        _table.EndLoadData();
        ApplyFilter();
        var listening = _ports.Count(p => p.IsListening);
        _statusLabel.Text = $"{listening} listening, {_ports.Count - listening} other connection(s).";
    }

    private void ApplyFilter()
    {
        var filter = _filterBox.Text.Trim()
            .Replace("[", "[[]")
            .Replace("%", "[%]")
            .Replace("*", "[*]")
            .Replace("'", "''");
        _table.DefaultView.RowFilter = filter.Length == 0
            ? ""
            : $"ProcessName LIKE '%{filter}%' OR LocalAddress LIKE '%{filter}%' OR RemoteAddress LIKE '%{filter}%' "
              + $"OR CONVERT(LocalPort, 'System.String') = '{filter}' OR CONVERT(RemotePort, 'System.String') = '{filter}' "
              + $"OR CONVERT(ProcessId, 'System.String') = '{filter}'";
    }
}
