using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentResults;

namespace WindowsUtils.Core.Net;

/// <summary>A TCP connection/listener or a UDP endpoint with the process that owns it.</summary>
public sealed record PortEntry(
    string Protocol,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int? RemotePort,
    string State,
    int ProcessId,
    string ProcessName)
{
    /// <summary>TCP in the Listen state, or any UDP endpoint (UDP has no connections).</summary>
    public bool IsListening => Protocol.StartsWith("UDP", StringComparison.Ordinal) || State == "Listen";
}

/// <summary>
/// Open ports and network connections with their owning process, read from the IP Helper
/// tables (GetExtendedTcpTable / GetExtendedUdpTable) for IPv4 and IPv6, like <c>netstat -ano</c>.
/// Read-only. The tables are read as raw bytes at fixed offsets (every field is a 4-byte DWORD or a
/// 16-byte address), so there is no struct layout that could differ between x64 and arm64.
/// </summary>
[SupportedOSPlatform("windows")]
public static class OpenPorts
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;

    /// <summary>All TCP (listeners and connections) and UDP endpoints, IPv4 and IPv6,
    /// sorted by protocol, then local port.</summary>
    public static Result<IReadOnlyList<PortEntry>> Get()
    {
        var names = new Dictionary<int, string>();
        return ReadTable(isTcp: true, AfInet)
            .Bind(tcp4 => ReadTable(isTcp: true, AfInet6)
                .Bind(tcp6 => ReadTable(isTcp: false, AfInet)
                    .Bind(udp4 => ReadTable(isTcp: false, AfInet6)
                        .Map(udp6 => (IReadOnlyList<PortEntry>)ParseTcp4(tcp4, names)
                            .Concat(ParseTcp6(tcp6, names))
                            .Concat(ParseUdp4(udp4, names))
                            .Concat(ParseUdp6(udp6, names))
                            .OrderBy(p => p.Protocol, StringComparer.Ordinal)
                            .ThenBy(p => p.LocalPort)
                            .ThenBy(p => p.RemoteAddress, StringComparer.Ordinal)
                            .ToList()))));
    }

    /// <summary>Human-readable TCP state names (MIB_TCP_STATE).</summary>
    public static string TcpStateName(uint state) => state switch
    {
        1 => "Closed",
        2 => "Listen",
        3 => "SynSent",
        4 => "SynReceived",
        5 => "Established",
        6 => "FinWait1",
        7 => "FinWait2",
        8 => "CloseWait",
        9 => "Closing",
        10 => "LastAck",
        11 => "TimeWait",
        12 => "DeleteTcb",
        _ => $"State {state}",
    };

    // The table as a byte copy: first DWORD is the row count, rows follow at offset 4.
    private static Result<byte[]> ReadTable(bool isTcp, int family)
    {
        var size = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = size > 0 ? Marshal.AllocHGlobal(size) : IntPtr.Zero;
            try
            {
                var error = isTcp
                    ? GetExtendedTcpTable(buffer, ref size, 0, family, TcpTableOwnerPidAll, 0)
                    : GetExtendedUdpTable(buffer, ref size, 0, family, UdpTableOwnerPid, 0);
                if (error == ErrorInsufficientBuffer)
                {
                    size += 4096; // connections may open between the size query and the read
                    continue;
                }
                if (error != NoError)
                    return Result.Fail($"Could not read the {(isTcp ? "TCP" : "UDP")}{(family == AfInet6 ? "v6" : "")} table (error {error}).");
                var bytes = new byte[size];
                Marshal.Copy(buffer, bytes, 0, size);
                return bytes;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
            }
        }
        return Result.Fail("The connection table kept changing size; try again.");
    }

    private static uint Dword(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset, 4));

    // Ports are stored in network byte order in the low 16 bits of a DWORD.
    private static int Port(byte[] b, int offset) => (b[offset] << 8) | b[offset + 1];

    private static int Count(byte[] b, int rowSize) =>
        b.Length < 4 ? 0 : (int)Math.Min(Dword(b, 0), (uint)((b.Length - 4) / rowSize));

    private static string V4(byte[] b, int offset) => new IPAddress(b.AsSpan(offset, 4)).ToString();

    private static string V6(byte[] b, int offset) => new IPAddress(b.AsSpan(offset, 16)).ToString();

    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (6 DWORDs).
    private static IEnumerable<PortEntry> ParseTcp4(byte[] b, Dictionary<int, string> names)
    {
        const int row = 24;
        for (var i = 0; i < Count(b, row); i++)
        {
            var o = 4 + i * row;
            var state = Dword(b, o);
            var pid = (int)Dword(b, o + 20);
            var listening = state == 2;
            yield return new PortEntry("TCP", V4(b, o + 4), Port(b, o + 8),
                listening ? "" : V4(b, o + 12), listening ? null : Port(b, o + 16),
                TcpStateName(state), pid, ProcessName(pid, names));
        }
    }

    // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid.
    private static IEnumerable<PortEntry> ParseTcp6(byte[] b, Dictionary<int, string> names)
    {
        const int row = 56;
        for (var i = 0; i < Count(b, row); i++)
        {
            var o = 4 + i * row;
            var state = Dword(b, o + 48);
            var pid = (int)Dword(b, o + 52);
            var listening = state == 2;
            yield return new PortEntry("TCPv6", V6(b, o), Port(b, o + 20),
                listening ? "" : V6(b, o + 24), listening ? null : Port(b, o + 44),
                TcpStateName(state), pid, ProcessName(pid, names));
        }
    }

    // MIB_UDPROW_OWNER_PID: localAddr, localPort, pid.
    private static IEnumerable<PortEntry> ParseUdp4(byte[] b, Dictionary<int, string> names)
    {
        const int row = 12;
        for (var i = 0; i < Count(b, row); i++)
        {
            var o = 4 + i * row;
            var pid = (int)Dword(b, o + 8);
            yield return new PortEntry("UDP", V4(b, o), Port(b, o + 4), "", null, "", pid, ProcessName(pid, names));
        }
    }

    // MIB_UDP6ROW_OWNER_PID: localAddr[16], localScope, localPort, pid.
    private static IEnumerable<PortEntry> ParseUdp6(byte[] b, Dictionary<int, string> names)
    {
        const int row = 28;
        for (var i = 0; i < Count(b, row); i++)
        {
            var o = 4 + i * row;
            var pid = (int)Dword(b, o + 24);
            yield return new PortEntry("UDPv6", V6(b, o), Port(b, o + 20), "", null, "", pid, ProcessName(pid, names));
        }
    }

    // Best effort: the process may have exited, or belong to another session (access denied).
    private static string ProcessName(int pid, Dictionary<int, string> cache)
    {
        if (cache.TryGetValue(pid, out var name))
            return name;
        name = pid switch
        {
            0 => "System Idle Process",
            4 => "System",
            _ => TryGetProcessName(pid),
        };
        cache[pid] = name;
        return name;
    }

    private static string TryGetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "";
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, int order, int family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, int order, int family, int tableClass, uint reserved);
}
