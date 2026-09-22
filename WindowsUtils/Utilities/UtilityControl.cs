using WindowsUtils.Core;

namespace WindowsUtils.Utilities;

/// <summary>Base class for all utility screens with shared UI helpers.</summary>
public abstract class UtilityControl : UserControl
{
    protected UtilityControl()
    {
        Dock = DockStyle.Fill;
    }

    protected static DataGridView CreateGrid()
    {
        return new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToOrderColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
        };
    }

    /// <summary>Adds a text column bound to a data source property.
    /// Explicit columns work even when DataSource is assigned before the control is parented
    /// (auto-generated columns are only created once the control gets a BindingContext).</summary>
    protected static DataGridViewTextBoxColumn AddBoundColumn(DataGridView grid, string propertyName, string header)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = propertyName,
            HeaderText = header,
            DataPropertyName = propertyName,
        };
        grid.Columns.Add(column);
        return column;
    }

    /// <summary>Disables sorting for grids that are filled manually (unbound).</summary>
    protected static void MakeUnsortable(DataGridView grid)
    {
        foreach (DataGridViewColumn column in grid.Columns)
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
    }

    protected static string FormatBytes(long bytes) => ByteFormatter.FormatBytes(bytes);
}
