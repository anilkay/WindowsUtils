using System.Data;
using WindowsUtils.Core.IO;

namespace WindowsUtils.Utilities;

public class FolderSizeControl : UtilityControl
{
    private readonly DataGridView _grid;
    private readonly DataTable _table = new();
    private readonly Label _statusLabel = new();
    private CancellationTokenSource? _cts;

    public FolderSizeControl()
    {
        _table.Columns.Add("Name", typeof(string));
        _table.Columns.Add("Type", typeof(string));
        _table.Columns.Add("Size", typeof(long));

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4),
        };

        var pathBox = new TextBox
        {
            PlaceholderText = "Select a folder...",
            Width = 420,
            Margin = new Padding(4, 8, 4, 4),
        };
        var browseButton = new Button { Text = "Browse...", AutoSize = true, Margin = new Padding(4, 6, 4, 4) };
        var analyzeButton = new Button { Text = "Analyze", AutoSize = true, Margin = new Padding(4, 6, 4, 4) };
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(12, 12, 4, 4);

        browseButton.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                pathBox.Text = dialog.SelectedPath;
                _ = AnalyzeAsync(dialog.SelectedPath);
            }
        };
        analyzeButton.Click += async (_, _) => await AnalyzeAsync(pathBox.Text.Trim());

        topPanel.Controls.Add(pathBox);
        topPanel.Controls.Add(browseButton);
        topPanel.Controls.Add(analyzeButton);
        topPanel.Controls.Add(_statusLabel);

        _grid = CreateGrid();
        _grid.DataSource = _table;
        _grid.CellFormatting += (_, e) =>
        {
            if (e.Value is long bytes && _grid.Columns[e.ColumnIndex].DataPropertyName == "Size")
            {
                e.Value = FormatBytes(bytes);
                e.FormattingApplied = true;
            }
        };

        Controls.Add(_grid);
        Controls.Add(topPanel);
    }

    private async Task AnalyzeAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            _statusLabel.Text = "Select an existing folder.";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _statusLabel.Text = "Analyzing...";
        _table.Rows.Clear();
        try
        {
            var rows = await Task.Run(() =>
            {
                var list = new List<(string Name, string Type, long Size)>();
                foreach (var directory in Directory.EnumerateDirectories(path))
                {
                    token.ThrowIfCancellationRequested();
                    list.Add((Path.GetFileName(directory), "Folder", FileScanner.GetDirectorySize(directory, token)));
                }
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    token.ThrowIfCancellationRequested();
                    long length;
                    try { length = new FileInfo(file).Length; } catch { length = 0; }
                    list.Add((Path.GetFileName(file), "File", length));
                }
                return list;
            }, token);

            foreach (var (name, type, size) in rows)
                _table.Rows.Add(name, type, size);
            _table.DefaultView.Sort = "Size DESC";

            _statusLabel.Text = $"Done: {rows.Count} items, total {FormatBytes(rows.Sum(r => r.Size))}.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Error: {ex.Message}";
        }
    }
}
