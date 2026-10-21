using System.Runtime.InteropServices;
using WindowsUtils.Core.SystemInfo;

namespace WindowsUtils.Utilities;

public class SystemInfoControl : UtilityControl
{
    public SystemInfoControl()
    {
        var grid = CreateGrid();
        grid.Columns.Add("Property", "Property");
        grid.Columns.Add("Value", "Value");
        MakeUnsortable(grid);

        foreach (var (name, value) in Collect())
            grid.Rows.Add(name, value);
        AddRuntimeRows(grid, ".NET Framework", NetFrameworkDetector.Find());
        AddRuntimeRows(grid, "Java", JavaDetector.Find());
        AddRuntimeRows(grid, "Python", PythonDetector.Find());
        AddRuntimeRows(grid, "Node.js", NodeDetector.Find());

        Controls.Add(grid);
        _ = AddFeatureRowsAsync(grid);
    }

    // WMI takes about a second, so the feature rows are filled in after the screen appears.
    private static async Task AddFeatureRowsAsync(DataGridView grid)
    {
        var loading = grid.Rows[grid.Rows.Add("Windows Features", "Checking...")];
        loading.Cells[1].Style.ForeColor = Theme.SubtleText;

        IReadOnlyList<WindowsFeature> features;
        try
        {
            features = await Task.Run(WindowsFeatures.Get);
        }
        catch (Exception ex)
        {
            features = [];
            loading.Cells[1].Value = $"Could not read: {ex.Message}";
        }
        if (grid.IsDisposed || features.Count == 0)
            return;

        grid.Rows.Remove(loading);
        foreach (var f in features)
        {
            var row = grid.Rows[grid.Rows.Add(f.Name, f.Display)];
            if (f.State != FeatureState.Enabled)
                row.Cells[1].Style.ForeColor = Theme.SubtleText;
        }
    }

    private static void AddRuntimeRows(DataGridView grid, string name, IReadOnlyList<RuntimeInstallation> installs)
    {
        if (installs.Count == 0)
        {
            var row = grid.Rows[grid.Rows.Add(name, "Not installed")];
            row.Cells[1].Style.ForeColor = Theme.SubtleText;
            return;
        }

        var main = installs[0];
        grid.Rows.Add(name, main.Display);
        grid.Rows.Add($"{name} Location", $"{main.Home}  ({main.Source})");
        if (installs.Count > 1)
            grid.Rows.Add($"Other {name} Installs", string.Join(", ", installs.Skip(1).Select(i => i.Display)));
    }

    private static IEnumerable<(string Name, string Value)> Collect()
    {
        yield return ("Operating System", RuntimeInformation.OSDescription);
        yield return ("OS Version", Environment.OSVersion.VersionString);
        yield return ("OS Architecture", RuntimeInformation.OSArchitecture.ToString());
        yield return ("Machine Name", Environment.MachineName);
        yield return ("Current User", $@"{Environment.UserDomainName}\{Environment.UserName}");
        yield return (".NET Runtime", RuntimeInformation.FrameworkDescription);
        yield return ("CPU", SystemDetails.CpuName());
        yield return ("Logical Processors", Environment.ProcessorCount.ToString());

        var (total, available) = SystemDetails.Memory();
        if (total > 0)
        {
            yield return ("Total RAM", FormatBytes((long)total));
            yield return ("Available RAM", FormatBytes((long)available));
            yield return ("RAM In Use", $"{100 - available * 100.0 / total:0.#}%");
        }

        var uptime = SystemDetails.Uptime();
        yield return ("System Uptime", $"{(int)uptime.TotalDays} days, {uptime.Hours}h {uptime.Minutes}m");
        yield return ("System Directory", SystemDetails.GetSystemDirectory());
        var (screenWidth, screenHeight) = SystemDetails.ScreenSize();
        yield return ("Screen Resolution", $"{screenWidth} x {screenHeight}");
    }
}
