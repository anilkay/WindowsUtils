using System.Data;
using WindowsUtils.Core.SystemInfo;

namespace WindowsUtils.Utilities;

public class InstalledProgramsControl : UtilityControl
{
    private readonly DataGridView _grid;
    private readonly DataTable _table = new();
    private readonly TextBox _filterBox = new() { PlaceholderText = "Filter by name or publisher...", Width = 260 };
    private readonly Button _refreshButton = new() { Text = "Refresh", AutoSize = true };
    private readonly Label _statusLabel = new();

    public InstalledProgramsControl()
    {
        // Typed columns, so clicking Size or Installed sorts by value, not by the formatted text.
        _table.Columns.Add("Name", typeof(string));
        _table.Columns.Add("Version", typeof(string));
        _table.Columns.Add("Publisher", typeof(string));
        _table.Columns.Add("Size", typeof(long));
        _table.Columns.Add("Installed", typeof(DateTime));
        _table.Columns.Add("Scope", typeof(string));
        _table.Columns.Add("Location", typeof(string));

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
        AddBoundColumn(_grid, "Name", "Name").FillWeight = 50;
        AddBoundColumn(_grid, "Version", "Version").FillWeight = 22;
        AddBoundColumn(_grid, "Publisher", "Publisher").FillWeight = 28;
        // Short values: sized to their content so they are never cut off; Name and Publisher share the rest.
        AddBoundColumn(_grid, "Size", "Size").AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        var installed = AddBoundColumn(_grid, "Installed", "Installed");
        installed.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        installed.DefaultCellStyle.Format = "d";
        AddBoundColumn(_grid, "Scope", "Scope").AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        AddBoundColumn(_grid, "Location", "Location").Visible = false;
        _grid.DataSource = _table;
        _grid.CellFormatting += (_, e) =>
        {
            if (e.Value is long bytes && _grid.Columns[e.ColumnIndex].DataPropertyName == "Size")
            {
                e.Value = FormatBytes(bytes);
                e.FormattingApplied = true;
            }
        };
        _grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].DataBoundItem is DataRowView row && row["Location"] is string { Length: > 0 } location)
                e.ToolTipText = location;
        };

        var note = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Text = "Read-only (registry Uninstall keys). Click a header to sort. Blank size/date: not recorded by the installer.",
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
        _statusLabel.Text = "Reading installed programs...";
        try
        {
            var programs = await Task.Run(InstalledPrograms.Get);
            _table.BeginLoadData();
            _table.Rows.Clear();
            foreach (var p in programs)
            {
                _table.Rows.Add(p.Name, p.Version, p.Publisher, p.SizeBytes is long size ? size : DBNull.Value,
                    p.InstallDate is DateTime date ? date : DBNull.Value, p.Scope, p.InstallLocation);
            }
            _table.EndLoadData();
            ApplyFilter();
            _statusLabel.Text = $"{programs.Count} programs, {FormatBytes(programs.Sum(p => p.SizeBytes ?? 0))} reported size.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Error: {ex.GetBaseException().Message}";
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
            : $"Name LIKE '%{filter}%' OR Publisher LIKE '%{filter}%'";
    }
}
