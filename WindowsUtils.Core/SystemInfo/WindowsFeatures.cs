using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WindowsUtils.Core.SystemInfo;

public enum FeatureState { Enabled, Disabled, NotAvailable, Unknown }

/// <summary>An optional Windows feature and, when it runs one, its service.</summary>
public sealed record WindowsFeature(string Name, string Id, FeatureState State, string? Service, string? ServiceState)
{
    public string Display => State switch
    {
        FeatureState.Enabled => "Enabled",
        FeatureState.Disabled => "Disabled",
        FeatureState.NotAvailable => "Not available on this Windows",
        _ => "Unknown",
    } + (Service is null ? "" : $"  (service {Service}: {ServiceState ?? "not installed"})");
}

/// <summary>
/// Read-only check of well-known optional Windows features (Telnet, IIS, SMB 1.0, Hyper-V, WSL, ...).
/// Uses WMI Win32_OptionalFeature / Win32_Service through the scripting COM object, so Core needs no
/// package; falls back to the servicing registry key when WMI is unavailable. Never changes anything.
/// </summary>
public static class WindowsFeatures
{
    // Id is the DISM / Win32_OptionalFeature name. Id null = not an optional feature (a capability),
    // detected by its service alone.
    private static readonly (string Name, string? Id, string? Service)[] Known =
    [
        ("Telnet Client", "TelnetClient", null),
        ("Telnet Server", "TelnetServer", "TlntSvr"),
        ("TFTP Client", "TFTP", null),
        ("IIS (Web Server)", "IIS-WebServerRole", "W3SVC"),
        ("IIS FTP Server", "IIS-FTPServer", "FTPSVC"),
        ("SMB 1.0 (CIFS)", "SMB1Protocol", null),
        ("Hyper-V", "Microsoft-Hyper-V", "vmms"),
        ("WSL (Windows Subsystem for Linux)", "Microsoft-Windows-Subsystem-Linux", null),
        ("Virtual Machine Platform", "VirtualMachinePlatform", null),
        ("Windows Sandbox", "Containers-DisposableClientVM", null),
        (".NET Framework 3.5", "NetFx3", null),
        ("OpenSSH Server", null, "sshd"),
    ];

    private const string ServicingKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Notifications\OptionalFeatures";

    /// <summary>Returns the state of each known feature. Slow-ish (WMI, ~1 s): call off the UI thread.</summary>
    public static IReadOnlyList<WindowsFeature> Get()
    {
        var result = new List<WindowsFeature>();
        if (!OperatingSystem.IsWindows())
            return result;

        var ids = Known.Where(k => k.Id is not null).Select(k => k.Id!).ToArray();
        var services = Known.Where(k => k.Service is not null).Select(k => k.Service!).ToArray();

        var featureStates = QueryWmi(
            $"SELECT Name, InstallState FROM Win32_OptionalFeature WHERE {string.Join(" OR ", ids.Select(i => $"Name='{i}'"))}",
            "Name", "InstallState");
        var serviceStates = QueryWmi(
            $"SELECT Name, State, StartMode FROM Win32_Service WHERE {string.Join(" OR ", services.Select(s => $"Name='{s}'"))}",
            "Name", "State", "StartMode");

        foreach (var (name, id, service) in Known)
        {
            string? serviceState = null;
            var svc = serviceStates?.FirstOrDefault(s => string.Equals(s[0], service, StringComparison.OrdinalIgnoreCase));
            if (svc is not null)
                serviceState = $"{svc[1]}, {svc[2]}";

            FeatureState state;
            if (id is null)
                state = svc is not null ? FeatureState.Enabled : serviceStates is null ? FeatureState.Unknown : FeatureState.NotAvailable;
            else if (featureStates is not null)
            {
                var row = featureStates.FirstOrDefault(f => string.Equals(f[0], id, StringComparison.OrdinalIgnoreCase));
                state = row?[1] switch
                {
                    "1" => FeatureState.Enabled,
                    "2" => FeatureState.Disabled,
                    null or "3" => FeatureState.NotAvailable, // 3 = Absent (payload removed / not offered)
                    _ => FeatureState.Unknown,
                };
            }
            else
                state = FromRegistry(id);

            result.Add(new WindowsFeature(name, id ?? service!, state, service, serviceState));
        }
        return result;
    }

    // Fallback: servicing only records features whose selection was changed, so a missing
    // key means "default" and is reported as Unknown rather than guessed.
    [SupportedOSPlatform("windows")]
    private static FeatureState FromRegistry(string id)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ServicingKey}\{id}");
            return key?.GetValue("Selection") switch
            {
                1 => FeatureState.Enabled,
                0 => FeatureState.Disabled,
                _ => FeatureState.Unknown,
            };
        }
        catch (Exception)
        {
            return FeatureState.Unknown;
        }
    }

    /// <summary>Runs a WQL query on root\cimv2; returns the requested properties as strings, or null if WMI fails.</summary>
    [SupportedOSPlatform("windows")]
    private static List<string?[]>? QueryWmi(string wql, params string[] properties)
    {
        object? locator = null, services = null, results = null;
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type is null)
                return null;
            locator = Activator.CreateInstance(type);
            dynamic loc = locator!;
            services = loc.ConnectServer(".", @"root\cimv2");
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
