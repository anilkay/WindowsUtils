using System.Collections;
using System.Data;

namespace WindowsUtils.Utilities;

public class EnvironmentVariablesControl : UtilityControl
{
    private readonly DataGridView _grid;
    private readonly DataTable _table = new();
    private readonly ComboBox _targetBox = new();

    public EnvironmentVariablesControl()
    {
        _table.Columns.Add("Name", typeof(string));
        _table.Columns.Add("Value", typeof(string));

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(4),
        };

        topPanel.Controls.Add(new Label { Text = "Target:", AutoSize = true, Margin = new Padding(4, 12, 4, 4) });
        _targetBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _targetBox.Items.AddRange(["User", "Machine", "Process"]);
        _targetBox.SelectedIndex = 0;
        _targetBox.Margin = new Padding(4, 8, 4, 4);
        _targetBox.SelectedIndexChanged += (_, _) => LoadVariables();
        topPanel.Controls.Add(_targetBox);

        _grid = CreateGrid();
        _grid.DataSource = _table;

        Controls.Add(_grid);
        Controls.Add(topPanel);

        LoadVariables();
    }

    private void LoadVariables()
    {
        var target = _targetBox.SelectedItem?.ToString() switch
        {
            "User" => EnvironmentVariableTarget.User,
            "Machine" => EnvironmentVariableTarget.Machine,
            _ => EnvironmentVariableTarget.Process,
        };

        _table.Rows.Clear();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables(target))
            _table.Rows.Add(entry.Key.ToString(), entry.Value?.ToString() ?? "");
        _table.DefaultView.Sort = "Name ASC";
    }
}
