using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>
/// Minimal read-only WMI access through the WbemScripting COM object, so Core needs no
/// System.Management package. Slow-ish (WMI): call off the UI thread.
/// </summary>
internal static class Wmi
{
    /// <summary>Runs a WQL query in <paramref name="wmiNamespace"/> (e.g. <c>root\cimv2</c>); returns the
    /// requested properties as strings, or null if WMI fails (service down, class missing, access denied).</summary>
    [SupportedOSPlatform("windows")]
    public static List<string?[]>? Query(string wmiNamespace, string wql, params string[] properties)
    {
        object? locator = null, services = null, results = null;
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type is null)
                return null;
            locator = Activator.CreateInstance(type);
            dynamic loc = locator!;
            services = loc.ConnectServer(".", wmiNamespace);
            dynamic svc = services!;
            results = svc.ExecQuery(wql);

            var rows = new List<string?[]>();
            foreach (dynamic item in (System.Collections.IEnumerable)results!)
            {
                try
                {
                    var row = new string?[properties.Length];
                    for (var i = 0; i < properties.Length; i++)
                        row[i] = item.Properties_.Item(properties[i]).Value?.ToString();
                    rows.Add(row);
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
            return rows;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            foreach (var o in new[] { results, services, locator })
                if (o is not null && Marshal.IsComObject(o))
                    Marshal.ReleaseComObject(o);
        }
    }
}
