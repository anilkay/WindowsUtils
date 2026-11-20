using System.Data;
using WindowsUtils.Core.SystemInfo;
using WindowsUtils.Localization;

namespace WindowsUtils.Utilities;

public class UpdateHistoryControl : UtilityControl
{
    private readonly DataGridView _grid;
    private readonly DataTable _table = new();
    private readonly TextBox _filterBox = new() { PlaceholderText = L.T("Filter by title or KB..."), Width = 260 };
    private readonly CheckBox _failedOnly = new() { Text = L.T("Failed only"), AutoSize = true };
    private readonly Button _refreshButton = new() { Text = L.T("Refresh"), AutoSize = true };
    private readonly Label _statusLabel = new();

    public UpdateHistoryControl()
    {
        _table.Columns.Add("Date", typeof(DateTime));
        _table.Columns.Add("Title", typeof(string));
        _table.Columns.Add("Kb", typeof(string));
        _table.Columns.Add("Operation", typeof(string));
        _table.Columns.Add("Result", typeof(string));
        _table.Columns.Add("Succeeded", typeof(bool));
        _table.Columns.Add("ErrorCode", typeof(string));

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
        _failedOnly.Margin = new Padding(12, 11, 4, 4);
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(12, 12, 4, 4);

        _refreshButton.Click += async (_, _) => await LoadAsync();
        _filterBox.TextChanged += (_, _) => ApplyFilter();
        _failedOnly.CheckedChanged += (_, _) => ApplyFilter();

        topPanel.Controls.Add(_refreshButton.AsAccent());
        topPanel.Controls.Add(_filterBox);
        topPanel.Controls.Add(_failedOnly);
        topPanel.Controls.Add(_statusLabel);

        _grid = CreateGrid();
        _grid.AutoGenerateColumns = false;
        var date = AddBoundColumn(_grid, "Date", L.T("Date"));
        date.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        date.DefaultCellStyle.Format = "g";
        var title = AddBoundColumn(_grid, "Title", L.T("Update"));
        title.FillWeight = 70;
        title.MinimumWidth = 240;
        AddBoundColumn(_grid, "Kb", "KB").AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        AddBoundColumn(_grid, "Operation", L.T("Operation")).AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        AddBoundColumn(_grid, "Result", L.T("Result")).AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        AddBoundColumn(_grid, "ErrorCode", L.T("Error Code")).AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        _grid.DataSource = _table;
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Columns[e.ColumnIndex].DataPropertyName == "Result"
                && _grid.Rows[e.RowIndex].DataBoundItem is DataRowView { Row: var row } && row["Succeeded"] is false)
            {
                e.CellStyle!.ForeColor = Theme.Danger;
            }
        };

        var note = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Text = L.T("Read-only (Windows Update Agent history, newest first, up to 500 entries)."),
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
        _statusLabel.Text = L.T("Reading Windows Update history...");
        try
        {
            var result = await Task.Run(() => WindowsUpdateHistory.Get(500));
            if (result.IsFailed)
            {
                _statusLabel.Text = result.Errors[0].Message;
                return;
            }
            var entries = result.Value;
            _table.BeginLoadData();
            _table.Rows.Clear();
            foreach (var e in entries)
                _table.Rows.Add(e.Date, e.Title, e.KbArticle, L.V(e.Operation), L.V(e.ResultText), e.Succeeded, e.ErrorCode);
            _table.EndLoadData();
            _table.DefaultView.Sort = "Date DESC";
            ApplyFilter();
            _statusLabel.Text = L.F("{0} entries, {1} failed.", entries.Count, entries.Count(e => !e.Succeeded));
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
        var parts = new List<string>();
        if (filter.Length > 0)
            parts.Add($"(Title LIKE '%{filter}%' OR Kb LIKE '%{filter}%')");
        if (_failedOnly.Checked)
            parts.Add("Succeeded = false");
        _table.DefaultView.RowFilter = string.Join(" AND ", parts);
    }
}
