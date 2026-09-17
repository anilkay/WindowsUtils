namespace WindowsUtils.Utilities;

public class DiskInfoControl : UtilityControl
{
    public DiskInfoControl()
    {
        var grid = CreateGrid();
        grid.Columns.Add("Drive", "Drive");
        grid.Columns.Add("Label", "Label");
        grid.Columns.Add("Type", "Type");
        grid.Columns.Add("Format", "Format");
        grid.Columns.Add("Total", "Total");
        grid.Columns.Add("Free", "Free");
        grid.Columns.Add("FreePercent", "Free %");
        MakeUnsortable(grid);

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                var freePercent = drive.TotalSize > 0
                    ? drive.AvailableFreeSpace * 100.0 / drive.TotalSize
                    : 0;
                var rowIndex = grid.Rows.Add(
                    drive.Name,
                    drive.VolumeLabel,
                    drive.DriveType.ToString(),
                    drive.DriveFormat,
                    FormatBytes(drive.TotalSize),
                    FormatBytes(drive.AvailableFreeSpace),
                    $"{freePercent:0.#}%");

                if (freePercent < 10)
                    grid.Rows[rowIndex].DefaultCellStyle.BackColor = Color.MistyRose;
            }
            catch (Exception)
            {
                grid.Rows.Add(drive.Name, "(not ready)", drive.DriveType.ToString(), "-", "-", "-", "-");
            }
        }

        Controls.Add(grid);
    }
}
