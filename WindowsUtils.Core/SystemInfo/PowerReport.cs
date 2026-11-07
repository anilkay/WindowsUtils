using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>One battery as reported by the battery driver (WMI root\wmi). Capacities are in mWh; any value may be missing.</summary>
public sealed record BatteryDetails(
    string Name,
    string? Manufacturer,
    string? Chemistry,
    long? DesignCapacity,
    long? FullChargeCapacity,
    long? RemainingCapacity,
    int? CycleCount,
    string? State,
    long? RateMilliwatts)
{
    /// <summary>Full-charge capacity as a percentage of the design capacity (battery wear).</summary>
    public double? HealthPercent => DesignCapacity is > 0 && FullChargeCapacity is > 0
        ? FullChargeCapacity.Value * 100.0 / DesignCapacity.Value
        : null;
}

/// <summary>Power source, charge, batteries and the active power plan.</summary>
public sealed record PowerReport(
    bool HasBattery,
    string PowerSource,
    int? ChargePercent,
    string ChargeState,
    TimeSpan? TimeRemaining,
    bool BatterySaverOn,
    IReadOnlyList<BatteryDetails> Batteries,
    string? PowerPlan,
    Guid? PowerPlanId);

/// <summary>
/// Read-only battery and power report: GetSystemPowerStatus for the live charge, WMI battery classes
/// for capacity/health/cycles, and powrprof for the active power plan. Never changes any setting.
/// </summary>
public static class PowerInfo
{
    /// <summary>Builds the report. WMI makes it slow-ish (~1 s): call off the UI thread.</summary>
    public static PowerReport Get()
    {
        if (!OperatingSystem.IsWindows())
            return new PowerReport(false, "Unknown", null, "Unknown", null, false, [], null, null);

        string source = "Unknown", state = "Unknown";
        int? percent = null;
        TimeSpan? remaining = null;
        var hasBattery = false;
        var saver = false;
        if (GetSystemPowerStatus(out var status))
        {
            source = status.ACLineStatus switch { 0 => "Battery", 1 => "AC power (plugged in)", _ => "Unknown" };
            hasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryFlag != 255;
            if (hasBattery)
            {
                percent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null;
                state = (status.BatteryFlag & 8) != 0 ? "Charging"
                    : status.ACLineStatus == 1 ? (percent == 100 ? "Fully charged" : "Plugged in, not charging")
                    : "Discharging";
                // -1 (0xFFFFFFFF) = unknown, e.g. while plugged in or right after unplugging.
                if (status.BatteryLifeTime is > 0 and < uint.MaxValue)
                    remaining = TimeSpan.FromSeconds(status.BatteryLifeTime);
            }
            else
            {
                state = "No battery";
            }
            saver = status.SystemStatusFlag == 1;
        }

        var batteries = hasBattery ? ReadBatteries() : [];
        if (batteries.Count > 0)
            hasBattery = true;
        var (plan, planId) = ReadActivePowerPlan();
        return new PowerReport(hasBattery, source, percent, state, remaining, saver, batteries, plan, planId);
    }

    // The battery classes share InstanceName (one per battery), so rows are joined on it.
    [SupportedOSPlatform("windows")]
    private static List<BatteryDetails> ReadBatteries()
    {
        var statics = Wmi.Query(@"root\wmi", "SELECT InstanceName, DeviceName, ManufactureName, Chemistry, DesignedCapacity FROM BatteryStaticData",
            "InstanceName", "DeviceName", "ManufactureName", "Chemistry", "DesignedCapacity");
        if (statics is null || statics.Count == 0)
            return [];
        var full = Lookup(Wmi.Query(@"root\wmi", "SELECT InstanceName, FullChargedCapacity FROM BatteryFullChargedCapacity",
            "InstanceName", "FullChargedCapacity"));
        var cycles = Lookup(Wmi.Query(@"root\wmi", "SELECT InstanceName, CycleCount FROM BatteryCycleCount",
            "InstanceName", "CycleCount"));
        var live = Wmi.Query(@"root\wmi", "SELECT InstanceName, RemainingCapacity, Charging, Discharging, ChargeRate, DischargeRate FROM BatteryStatus",
            "InstanceName", "RemainingCapacity", "Charging", "Discharging", "ChargeRate", "DischargeRate");

        var result = new List<BatteryDetails>();
        foreach (var s in statics)
        {
            var id = s[0] ?? "";
            var row = live?.FirstOrDefault(l => l[0] == id);
            string? state = null;
            long? rate = null;
            if (row is not null)
            {
                if (row[2] == "True")
                {
                    state = "Charging";
                    rate = ParseLong(row[4]);
                }
                else if (row[3] == "True")
                {
                    state = "Discharging";
                    rate = ParseLong(row[5]);
                }
                else
                {
                    state = "Idle";
                }
            }

            result.Add(new BatteryDetails(
                Name: string.IsNullOrWhiteSpace(s[1]) ? $"Battery {result.Count + 1}" : s[1]!.Trim(),
                Manufacturer: string.IsNullOrWhiteSpace(s[2]) ? null : s[2]!.Trim(),
                Chemistry: DecodeChemistry(s[3]),
                DesignCapacity: Positive(ParseLong(s[4])),
                FullChargeCapacity: Positive(ParseLong(full.GetValueOrDefault(id))),
                RemainingCapacity: ParseLong(row?[1]),
                CycleCount: (int?)Positive(ParseLong(cycles.GetValueOrDefault(id))),
                State: state,
                RateMilliwatts: rate is > 0 ? rate : null));
        }
        return result;
    }

    private static Dictionary<string, string?> Lookup(List<string?[]>? rows)
    {
        var map = new Dictionary<string, string?>();
        foreach (var row in rows ?? [])
            map[row[0] ?? ""] = row[1];
        return map;
    }

    private static long? ParseLong(string? value) => long.TryParse(value, out var number) ? number : null;

    // Drivers report 0 (or 0xFFFFFFFF) for values they do not know.
    private static long? Positive(long? value) => value is > 0 and < uint.MaxValue ? value : null;

    // Chemistry is four ASCII characters packed into a little-endian uint, e.g. "LION", "LiP ".
    private static string? DecodeChemistry(string? value)
    {
        if (!uint.TryParse(value, out var packed) || packed == 0)
            return null;
        var text = Encoding.ASCII.GetString(BitConverter.GetBytes(packed)).Trim('\0', ' ');
        return text.Length > 0 && text.All(c => c is >= ' ' and <= '~') ? text : null;
    }

    [SupportedOSPlatform("windows")]
    private static (string? Name, Guid? Id) ReadActivePowerPlan()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var schemePtr) != 0 || schemePtr == IntPtr.Zero)
                return (null, null);
            Guid scheme;
            try
            {
                scheme = Marshal.PtrToStructure<Guid>(schemePtr);
            }
            finally
            {
                LocalFree(schemePtr);
            }

            uint size = 0;
            if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size == 0)
                return (null, scheme);
            var buffer = new byte[size];
            if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
                return (null, scheme);
            return (Encoding.Unicode.GetString(buffer).TrimEnd('\0'), scheme);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid,
        IntPtr powerSettingGuid, byte[]? buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
