using WindowsUtils.Localization;
namespace WindowsUtils.Utilities;

public class DiskInfoControl : UtilityControl
{
    public DiskInfoControl()
    {
        var grid = CreateGrid();
        grid.Columns.Add("Drive", L.T("Drive"));
        grid.Columns.Add("Label", L.T("Label"));
        grid.Columns.Add("Type", L.T("Type"));
        grid.Columns.Add("Format", L.T("Format"));
        grid.Columns.Add("Total", L.T("Total"));
        grid.Columns.Add("Free", L.T("Free"));
        grid.Columns.Add("FreePercent", L.T("Free %"));
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
                    grid.Rows[rowIndex].DefaultCellStyle.BackColor = Theme.DangerBackground;
            }
            catch (Exception)
            {
                grid.Rows.Add(drive.Name, L.T("(not ready)"), L.V(drive.DriveType.ToString()), "-", "-", "-", "-");
            }
        }

        Controls.Add(grid);
    }
}
