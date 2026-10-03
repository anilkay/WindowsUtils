using System.Runtime.Versioning;

namespace WindowsUtils.Core.Net;

/// <summary>An enabled inbound "allow" rule that opens TCP/UDP ports.</summary>
public sealed record FirewallPortRule(
    string Name,
    string Protocol,
    string LocalPorts,
    string Application,
    string Profiles);

/// <summary>
/// Reads Windows Firewall rules through the WFP COM interface (HNetCfg.FwPolicy2),
/// which is locale-independent (unlike parsing netsh output). Read-only.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FirewallRules
{
    private const int DirectionIn = 1;
    private const int ActionAllow = 1;
    private const int ProtocolTcp = 6;
    private const int ProtocolUdp = 17;
    private const int ProtocolAny = 256;

    private const int ProfileDomain = 1;
    private const int ProfilePrivate = 2;
    private const int ProfilePublic = 4;

    /// <summary>Enabled + Allow + inbound rules with TCP/UDP ports, sorted by protocol then port.</summary>
    public static IReadOnlyList<FirewallPortRule> GetInboundAllowRules()
    {
        var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
            ?? throw new InvalidOperationException("Windows Firewall COM interface (HNetCfg.FwPolicy2) is not available.");
        var instance = Activator.CreateInstance(policyType)
            ?? throw new InvalidOperationException("Could not create the Windows Firewall COM object.");
        dynamic policy = instance;

        var result = new List<FirewallPortRule>();
        try
        {
            foreach (dynamic rule in policy.Rules)
            {
                try
                {
                    if (!(bool)rule.Enabled)
                        continue;
                    if ((int)rule.Action != ActionAllow)
                        continue;
                    if ((int)rule.Direction != DirectionIn)
                        continue;

                    var protocol = (int)rule.Protocol;
                    string proto;
                    if (protocol == ProtocolTcp) proto = "TCP";
                    else if (protocol == ProtocolUdp) proto = "UDP";
                    else if (protocol == ProtocolAny) proto = "Any";
                    else continue; // ICMP and friends are not port-based

                    var ports = (string?)rule.LocalPorts;
                    result.Add(new FirewallPortRule(
                        (string)rule.Name,
                        proto,
                        string.IsNullOrWhiteSpace(ports) || ports == "*" ? "All ports" : ports,
                        FirstNonEmpty((string?)rule.ApplicationName, (string?)rule.ServiceName),
                        FormatProfiles((int)rule.Profiles)));
                }
                catch (Exception)
                {
                    // Unreadable rule (e.g. edge cases in COM marshalling): skip it.
                }
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(instance);
        }

        return result
            .OrderBy(r => r.Protocol, StringComparer.Ordinal)
            .ThenBy(r => FirstPort(r.LocalPorts))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return "";
    }

    private static string FormatProfiles(int profiles)
    {
        if ((profiles & (ProfileDomain | ProfilePrivate | ProfilePublic)) == (ProfileDomain | ProfilePrivate | ProfilePublic))
            return "All";
        var names = new List<string>(3);
        if ((profiles & ProfileDomain) != 0) names.Add("Domain");
        if ((profiles & ProfilePrivate) != 0) names.Add("Private");
        if ((profiles & ProfilePublic) != 0) names.Add("Public");
        return names.Count == 0 ? "-" : string.Join(", ", names);
    }

    private static int FirstPort(string localPorts)
    {
        // "80", "80,443", "5000-6000" -> leading number; "All ports" -> 0.
        var digits = localPorts.TakeWhile(char.IsDigit).ToArray();
        return digits.Length == 0 ? 0 : int.Parse(digits);
    }
}
