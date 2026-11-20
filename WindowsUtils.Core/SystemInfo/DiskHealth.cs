using System.Globalization;
using System.Runtime.Versioning;
using FluentResults;

namespace WindowsUtils.Core.SystemInfo;

/// <summary>Health of one physical disk: Windows' own verdict plus the drive's reliability counters
/// (SMART-derived; null when the drive or driver does not report them, or without Administrator).</summary>
public sealed record DiskHealthInfo(
    string DeviceId,
    string Name,
    string MediaType,
    string BusType,
    long SizeBytes,
    string Health,
    string OperationalStatus,
    bool? PredictsFailure,
    int? TemperatureC,
    int? TemperatureMaxC,
    int? WearPercent,
    long? PowerOnHours,
    long? ReadErrorsUncorrected,
    long? WriteErrorsUncorrected,
    long? StartStopCycles)
{
    /// <summary>True when Windows or the drive reports anything other than healthy.</summary>
    public bool NeedsAttention =>
        Health != "Healthy" || PredictsFailure == true || ReadErrorsUncorrected > 0 || WriteErrorsUncorrected > 0 || WearPercent >= 90;
}

/// <summary>
/// Drive health from the Storage Management WMI provider (root\Microsoft\Windows\Storage:
/// MSFT_PhysicalDisk and MSFT_StorageReliabilityCounter, what Settings › Disks shows) and the
/// SMART failure prediction flag (root\wmi MSStorageDriver_FailurePredictStatus). Read-only.
/// Reliability counters and SMART need Administrator; without it those columns stay empty.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DiskHealth
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    public static Result<IReadOnlyList<DiskHealthInfo>> Get()
    {
        var disks = Wmi.Query(StorageNamespace,
            "SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk",
            "DeviceId", "FriendlyName", "MediaType", "BusType", "Size", "HealthStatus", "OperationalStatus");
        if (disks is null)
            return Result.Fail("Could not read physical disks from the Storage Management provider (WMI).");

        // Optional: both are empty for a standard user or for drives that report nothing.
        var counters = (Wmi.Query(StorageNamespace,
                "SELECT DeviceId, Temperature, TemperatureMax, Wear, PowerOnHours, ReadErrorsUncorrected, "
                + "WriteErrorsUncorrected, StartStopCycleCount FROM MSFT_StorageReliabilityCounter",
                "DeviceId", "Temperature", "TemperatureMax", "Wear", "PowerOnHours", "ReadErrorsUncorrected",
                "WriteErrorsUncorrected", "StartStopCycleCount") ?? [])
            .Where(r => r[0] is not null)
            .GroupBy(r => r[0]!)
            .ToDictionary(g => g.Key, g => g.First());
        var smart = Wmi.Query(@"root\wmi", "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus",
            "InstanceName", "PredictFailure") ?? [];
        bool? anySmartFailure = smart.Count == 0 ? null : smart.Any(r => IsTrue(r[1]));

        return disks.Select(d =>
            {
                var id = d[0] ?? "";
                counters.TryGetValue(id, out var c);
                return new DiskHealthInfo(
                    id,
                    d[1] ?? "(unnamed disk)",
                    MediaTypeName(Int(d[2])),
                    BusTypeName(Int(d[3])),
                    Long(d[4]) ?? 0,
                    HealthName(Int(d[5])),
                    OperationalStatusName(d[6]),
                    // MSStorageDriver_FailurePredictStatus has no disk number that matches DeviceId
                    // reliably, so a single disk gets the flag and several disks only an "any" flag.
                    disks.Count == 1 ? anySmartFailure : (anySmartFailure == true ? true : null),
                    Int(c?[1]) is > 0 and var t ? t : null,
                    Int(c?[2]) is > 0 and var tm ? tm : null,
                    Int(c?[3]),
                    Long(c?[4]),
                    Long(c?[5]),
                    Long(c?[6]),
                    Long(c?[7]));
            })
            .OrderBy(d => int.TryParse(d.DeviceId, out var n) ? n : int.MaxValue)
            .ToList();
    }

    private static int? Int(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static long? Long(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static bool IsTrue(string? value) => string.Equals(value, "True", StringComparison.OrdinalIgnoreCase);

    private static string MediaTypeName(int? value) => value switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "Unspecified",
    };

    private static string BusTypeName(int? value) => value switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "Unknown",
    };

    private static string HealthName(int? value) => value switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => "Unknown",
    };

    // OperationalStatus is an array of codes; show the known ones by name.
    private static string OperationalStatusName(string? codes)
    {
        if (string.IsNullOrEmpty(codes))
            return "Unknown";
        return string.Join(", ", codes.Split(',').Select(code => Int(code.Trim()) switch
        {
            2 => "OK",
            3 => "Degraded",
            4 => "Stressed",
            5 => "Predictive failure",
            6 => "Error",
            7 => "Non-recoverable error",
            8 => "Starting",
            9 => "Stopping",
            10 => "Stopped",
            11 => "In service",
            12 => "No contact",
            13 => "Lost communication",
            0xD010 => "Online",
            0xD011 => "Not ready",
            0xD012 => "No media",
            0xD013 => "Offline",
            0xD014 => "Failed",
            _ => $"Status {code.Trim()}",
        }));
    }
}
