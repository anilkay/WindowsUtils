using System.Runtime.InteropServices;
using WindowsUtils.Core.SystemInfo;
using WindowsUtils.Localization;

namespace WindowsUtils.Utilities;

public class SystemInfoControl : UtilityControl
{
    public SystemInfoControl()
    {
        var grid = CreateGrid();
        grid.Columns.Add("Property", L.T("Property"));
        grid.Columns.Add("Value", L.T("Value"));
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
        var loading = grid.Rows[grid.Rows.Add(L.T("Windows Features"), L.T("Checking..."))];
        loading.Cells[1].Style.ForeColor = Theme.SubtleText;

        IReadOnlyList<WindowsFeature> features;
        try
        {
            features = await Task.Run(WindowsFeatures.Get);
        }
        catch (Exception ex)
        {
            features = [];
            loading.Cells[1].Value = L.F("Could not read: {0}", ex.Message);
        }
        if (grid.IsDisposed || features.Count == 0)
            return;

        grid.Rows.Remove(loading);
        foreach (var f in features)
        {
            var display = L.V(f.State switch
            {
                FeatureState.Enabled => "Enabled",
                FeatureState.Disabled => "Disabled",
                FeatureState.NotAvailable => "Not available on this Windows",
                _ => "Unknown",
            }) + (f.Service is null ? "" : "  " + L.F("(service {0}: {1})", f.Service, f.ServiceState ?? L.T("Not installed")));
            var row = grid.Rows[grid.Rows.Add(f.Name, display)];
            if (f.State != FeatureState.Enabled)
                row.Cells[1].Style.ForeColor = Theme.SubtleText;
        }
    }

    private static void AddRuntimeRows(DataGridView grid, string name, IReadOnlyList<RuntimeInstallation> installs)
    {
        if (installs.Count == 0)
        {
            var row = grid.Rows[grid.Rows.Add(name, L.T("Not installed"))];
            row.Cells[1].Style.ForeColor = Theme.SubtleText;
            return;
        }

        var main = installs[0];
        grid.Rows.Add(name, main.Display);
        grid.Rows.Add(L.F("{0} Location", name), $"{main.Home}  ({main.Source})");
        if (installs.Count > 1)
            grid.Rows.Add(L.F("Other {0} Installs", name), string.Join(", ", installs.Skip(1).Select(i => i.Display)));
    }

    private static IEnumerable<(string Name, string Value)> Collect()
    {
        yield return (L.T("Operating System"), RuntimeInformation.OSDescription);
        yield return (L.T("OS Version"), Environment.OSVersion.VersionString);
        yield return (L.T("OS Architecture"), RuntimeInformation.OSArchitecture.ToString());
        yield return (L.T("Machine Name"), Environment.MachineName);
        yield return (L.T("Current User"), $@"{Environment.UserDomainName}\{Environment.UserName}");
        yield return (L.T(".NET Runtime"), RuntimeInformation.FrameworkDescription);
        yield return ("CPU", SystemDetails.CpuName());
        yield return (L.T("Logical Processors"), Environment.ProcessorCount.ToString());

        var (total, available) = SystemDetails.Memory();
        if (total > 0)
        {
            yield return (L.T("Total RAM"), FormatBytes((long)total));
            yield return (L.T("Available RAM"), FormatBytes((long)available));
            yield return (L.T("RAM In Use"), $"{100 - available * 100.0 / total:0.#}%");
        }

        var uptime = SystemDetails.Uptime();
        yield return (L.T("System Uptime"), L.F("{0} days, {1}h {2}m", (int)uptime.TotalDays, uptime.Hours, uptime.Minutes));
        yield return (L.T("System Directory"), SystemDetails.GetSystemDirectory());
        var (screenWidth, screenHeight) = SystemDetails.ScreenSize();
        yield return (L.T("Screen Resolution"), $"{screenWidth} x {screenHeight}");
    }
}
