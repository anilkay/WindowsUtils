using System.Runtime.Versioning;
using System.Security;
using FluentResults;
using Microsoft.Win32;

namespace WindowsUtils.Core.Net;

/// <summary>
/// Whether Windows (Schannel) offers one protocol on one side.
/// <see cref="State"/> is "Enabled", "Disabled", "Disabled by default" (only apps that ask for it
/// explicitly get it) or "Not supported"; <see cref="Source"/> is "Registry" when an administrator
/// set it under SCHANNEL\Protocols, or "OS default" when nothing is set and this Windows build decides.
/// </summary>
public sealed record SchannelProtocolSetting(string Protocol, string Side, string State, string Source)
{
    /// <summary>True for an outdated protocol that is on, or a current one that is off.</summary>
    public bool NeedsAttention =>
        Protocol is "SSL 3.0" or "TLS 1.0" or "TLS 1.1"
            ? State == "Enabled"
            : State is "Disabled" or "Disabled by default";
}

/// <summary>
/// Read-only view of the Schannel protocol switches in
/// HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\&lt;protocol&gt;\Client|Server
/// (values <c>Enabled</c> and <c>DisabledByDefault</c>). When a value is missing, the Windows build's
/// default applies (Microsoft's "Protocols in TLS/SSL (Schannel SSP)" table).
/// </summary>
[SupportedOSPlatform("windows")]
public static class SchannelProtocols
{
    private const string ProtocolsKey = @"SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols";

    // Registry key name, display name.
    private static readonly (string Key, string Name)[] Protocols =
    [
        ("SSL 3.0", "SSL 3.0"),
        ("TLS 1.0", "TLS 1.0"),
        ("TLS 1.1", "TLS 1.1"),
        ("TLS 1.2", "TLS 1.2"),
        ("TLS 1.3", "TLS 1.3"),
    ];

    public static readonly string[] Sides = ["Client", "Server"];

    /// <summary>One setting per protocol and side, in protocol order (client first).</summary>
    public static Result<IReadOnlyList<SchannelProtocolSetting>> Get() =>
        OpenProtocolsKey().Map(root =>
        {
            using (root)
            {
                var build = Environment.OSVersion.Version.Build;
                IReadOnlyList<SchannelProtocolSetting> settings =
                [
                    .. from p in Protocols
                       from side in Sides
                       select Read(root, p.Key, p.Name, side, build),
                ];
                return settings;
            }
        });

    // A missing Protocols key is normal (nothing configured): every protocol keeps its OS default.
    private static Result<RegistryKey?> OpenProtocolsKey()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return Result.Ok(baseKey.OpenSubKey(ProtocolsKey));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return Result.Fail(new ExceptionalError($"Could not read the Schannel settings: {ex.Message}", ex));
        }
    }

    private static SchannelProtocolSetting Read(RegistryKey? root, string key, string name, string side, int build)
    {
        int? enabled = null, disabledByDefault = null;
        try
        {
            using var sideKey = root?.OpenSubKey($@"{key}\{side}");
            enabled = sideKey?.GetValue("Enabled") as int?;
            disabledByDefault = sideKey?.GetValue("DisabledByDefault") as int?;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unreadable subkey: fall back to the OS default, like an unset one.
        }

        var osDefault = OsDefault(name, build);
        // TLS 1.3 cannot be switched on where Schannel does not implement it.
        if (osDefault == "Not supported")
            return new(name, side, osDefault, "OS default");
        if (enabled == 0)
            return new(name, side, "Disabled", "Registry");
        if (disabledByDefault is { } d && d != 0)
            return new(name, side, "Disabled by default", "Registry");
        if (enabled is not null || disabledByDefault is not null)
            return new(name, side, "Enabled", "Registry");
        return new(name, side, osDefault, "OS default");
    }

    // Defaults per Windows build. .NET 10 needs Windows 10 / Server 2016 or later, so older
    // systems are not covered. Client and server share the same default on these builds.
    private static string OsDefault(string protocol, int build) => protocol switch
    {
        // SSL 3.0 is off by default since Windows 10 1607 / Server 2016 (build 14393).
        "SSL 3.0" => build >= 14393 ? "Disabled" : "Enabled",
        // TLS 1.0/1.1 are still on by default in released Windows builds (Microsoft's table lists
        // them as enabled up to Windows 11 24H2 / Server 2025); only Insider builds turn them off.
        "TLS 1.0" or "TLS 1.1" => "Enabled",
        "TLS 1.2" => "Enabled",
        // TLS 1.3 ships enabled from Windows Server 2022 (20348) and Windows 11 (22000).
        "TLS 1.3" => build >= 20348 ? "Enabled" : "Not supported",
        _ => "Enabled",
    };
}
