using System.Data;
using WindowsUtils.Core.Net;
using WindowsUtils.Localization;

namespace WindowsUtils.Utilities;

public class FirewallPortsControl : UtilityControl
{
    private readonly DataGridView _grid;
    private readonly DataTable _table = new();
    private readonly TextBox _filterBox = new() { PlaceholderText = L.T("Filter by rule name or port..."), Width = 260 };
    private readonly Button _refreshButton = new() { Text = L.T("Refresh"), AutoSize = true };
    private readonly Label _statusLabel = new();

    public FirewallPortsControl()
    {
        _table.Columns.Add("Protocol", typeof(string));
        _table.Columns.Add("LocalPorts", typeof(string));
        _table.Columns.Add("Name", typeof(string));
        _table.Columns.Add("Application", typeof(string));
        _table.Columns.Add("Profiles", typeof(string));

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
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(12, 12, 4, 4);

        _refreshButton.Click += async (_, _) => await LoadAsync();
        _filterBox.TextChanged += (_, _) => ApplyFilter();

        topPanel.Controls.Add(_refreshButton.AsAccent());
        topPanel.Controls.Add(_filterBox);
        topPanel.Controls.Add(_statusLabel);

        _grid = CreateGrid();
        _grid.AutoGenerateColumns = false;
        AddBoundColumn(_grid, "Protocol", L.T("Protocol")).FillWeight = 10;
        AddBoundColumn(_grid, "LocalPorts", L.T("Local Ports")).FillWeight = 22;
        AddBoundColumn(_grid, "Name", L.T("Rule Name")).FillWeight = 38;
        AddBoundColumn(_grid, "Application", L.T("Application / Service")).FillWeight = 30;
        AddBoundColumn(_grid, "Profiles", L.T("Profiles")).FillWeight = 16;
        _grid.DataSource = _table;

        var note = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Text = L.T("Read-only view of enabled inbound allow rules (Windows Firewall)."),
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
        _statusLabel.Text = L.T("Reading firewall rules...");
        try
        {
            var rules = await Task.Run(FirewallRules.GetInboundAllowRules);
            _table.Rows.Clear();
            foreach (var rule in rules)
                _table.Rows.Add(rule.Protocol, rule.LocalPorts, rule.Name, rule.Application, rule.Profiles);
            _table.DefaultView.Sort = "Protocol ASC, Name ASC";
            ApplyFilter();
            _statusLabel.Text = L.F("{0} enabled inbound allow rule(s).", rules.Count);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = L.F("Error: {0}", ex.GetBaseException().Message);
        }
        finally
        {
            _refreshButton.Enabled = true;
        }
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
            : $"Name LIKE '%{filter}%' OR LocalPorts LIKE '%{filter}%'";
    }
}
