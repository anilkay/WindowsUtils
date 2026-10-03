using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WindowsUtils.Core.Net;

/// <summary>One completed hop of a TCP traceroute. <see cref="Address"/> is null when the hop did not answer ("*").</summary>
public sealed record TracerouteHop(
    int Ttl,
    string? Address,
    string? HostName,
    long? Rtt1Ms,
    long? Rtt2Ms,
    long? Rtt3Ms,
    string? Note);

/// <summary>
/// TCP traceroute: sends TCP SYN probes with increasing TTL (hop limit 1, 2, ...)
/// and maps each hop from the ICMP Time Exceeded replies.
/// Needs Administrator privileges (raw socket for ICMP replies).
/// </summary>
public static class TcpTraceroute
{
    private const int ProbesPerHop = 3;

    private sealed record IcmpReply(
        string HopAddress, int Type, int Code,
        int SourcePort, int DestPort, IPAddress DestAddress,
        long ReceivedTimestamp);

    /// <summary>Traces the route to <paramref name="host"/>:<paramref name="port"/>, reporting each finished hop via <paramref name="progress"/>.</summary>
    public static async Task<IReadOnlyList<TracerouteHop>> TraceAsync(
        string host,
        int port,
        int maxHops = 30,
        int timeoutMs = 3000,
        bool resolveHostNames = true,
        IProgress<TracerouteHop>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be 1-65535.");
        maxHops = Math.Clamp(maxHops, 1, 255);
        timeoutMs = Math.Clamp(timeoutMs, 100, 60000);

        var target = await ResolveIPv4Async(host, cancellationToken);
        var localAddress = GetLocalAddress(target, port);

        using var listener = CreateIcmpListener(localAddress);
        using var _ = cancellationToken.Register(() => { try { listener.Close(); } catch { } });
        var replies = new ConcurrentQueue<IcmpReply>();
        var listenTask = Task.Run(() => ListenLoop(listener, replies, cancellationToken), CancellationToken.None);

        try
        {
            var hops = new List<TracerouteHop>();
            for (var ttl = 1; ttl <= maxHops; ttl++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hop = await TraceHopAsync(target, port, localAddress, ttl, timeoutMs,
                    resolveHostNames, replies, cancellationToken);
                hops.Add(hop);
                progress?.Report(hop);
                if (hop.Note == "Reached")
                    break;
            }
            return hops;
        }
        finally
        {
            try { listener.Close(); } catch { }
            try { await listenTask; } catch (SocketException) { } catch (ObjectDisposedException) { }
        }
    }

    private static async Task<IPAddress> ResolveIPv4Async(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Enter a host name or IP address.", nameof(host));
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host.Trim(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Could not resolve '{host}'.", ex);
        }
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException($"Could not resolve an IPv4 address for '{host}'.");
    }

    private static IPAddress GetLocalAddress(IPAddress target, int port)
    {
        // UDP "connect" sends nothing; it just selects the outbound interface.
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Connect(target, port);
        return ((IPEndPoint)udp.LocalEndPoint!).Address;
    }

    private static Socket CreateIcmpListener(IPAddress localAddress)
    {
        try
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
            listener.Bind(new IPEndPoint(localAddress, 0));
            return listener;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            throw new InvalidOperationException(
                "TCP traceroute needs Administrator privileges (raw socket for ICMP replies). " +
                "Restart the app as administrator and try again.", ex);
        }
    }

    private static void ListenLoop(Socket listener, ConcurrentQueue<IcmpReply> replies, CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        while (!cancellationToken.IsCancellationRequested)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int received;
            try
            {
                received = listener.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException)
            {
                break; // socket closed (done or cancelled)
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            var hopAddress = ((IPEndPoint)remote).Address.ToString();
            if (TryParseIcmp(buffer, received, hopAddress, Stopwatch.GetTimestamp(), out var reply) && reply is not null)
                replies.Enqueue(reply);
        }
    }

    private static bool TryParseIcmp(byte[] buffer, int length, string hopAddress, long timestamp, out IcmpReply? reply)
    {
        reply = null;
        if (length < 8)
            return false;
        var type = buffer[0];
        if (type is not (11 or 3)) // 11 = Time Exceeded, 3 = Destination Unreachable
            return false;
        var code = buffer[1];
        if (length < 8 + 20)
            return false;
        var headerLength = (buffer[8] & 0x0F) * 4; // embedded original IPv4 header
        if (headerLength < 20 || length < 8 + headerLength + 8)
            return false;
        var sourcePort = (buffer[8 + headerLength] << 8) | buffer[8 + headerLength + 1];
        var destPort = (buffer[8 + headerLength + 2] << 8) | buffer[8 + headerLength + 3];
        var destAddress = new IPAddress([buffer[8 + 16], buffer[8 + 17], buffer[8 + 18], buffer[8 + 19]]);
        reply = new IcmpReply(hopAddress, type, code, sourcePort, destPort, destAddress, timestamp);
        return true;
    }

    private static async Task<TracerouteHop> TraceHopAsync(
        IPAddress target, int port, IPAddress localAddress, int ttl, int timeoutMs,
        bool resolveHostNames, ConcurrentQueue<IcmpReply> replies, CancellationToken cancellationToken)
    {
        var rtts = new long?[ProbesPerHop];
        string? hopAddress = null;
        string? note = null;
        var reached = false;

        for (var probe = 0; probe < ProbesPerHop; probe++)
        {
            var (probeReached, rtt, icmp) = await ProbeOnceAsync(
                target, port, localAddress, ttl, timeoutMs, replies, cancellationToken);
            rtts[probe] = rtt;
            if (probeReached)
                reached = true;
            else if (icmp is not null)
            {
                hopAddress ??= icmp.HopAddress;
                note ??= icmp.Type == 3 ? UnreachableNote(icmp.Code) : null;
            }
        }

        if (reached)
            hopAddress ??= target.ToString(); // SYN-ACK/RST came from the target itself
        string? hostName = null;
        if (resolveHostNames && hopAddress is not null)
            hostName = await TryResolveHostNameAsync(hopAddress, cancellationToken);

        return new TracerouteHop(ttl, hopAddress, hostName, rtts[0], rtts[1], rtts[2],
            reached ? "Reached" : note);
    }

    private static async Task<(bool Reached, long? RttMs, IcmpReply? Icmp)> ProbeOnceAsync(
        IPAddress target, int port, IPAddress localAddress, int ttl, int timeoutMs,
        ConcurrentQueue<IcmpReply> replies, CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(localAddress, 0));
        var localPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);

        var sent = Stopwatch.GetTimestamp();
        var connectTask = socket.ConnectAsync(target, port, cancellationToken).AsTask();
        var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs, cancellationToken));

        if (completed == connectTask)
        {
            try
            {
                await connectTask;
                return (true, ElapsedMs(sent), null); // port open: reached
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                return (true, ElapsedMs(sent), null); // RST from target: reached
            }
            catch (SocketException)
            {
                // e.g. unreachable surfaced by the stack — check the ICMP queue below.
            }
        }
        else
        {
            // Timeout (or cancelled): stop the pending connect.
            try { socket.Close(); } catch { }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await connectTask;
                return (true, ElapsedMs(sent), null); // connected just as we timed out
            }
            catch { /* expected after Close — fall through to ICMP */ }
        }

        // Drain replies for this probe (ports + embedded target address must match).
        IcmpReply? match = null;
        while (replies.TryDequeue(out var reply))
        {
            if (match is null
                && reply.ReceivedTimestamp >= sent
                && reply.SourcePort == localPort
                && reply.DestPort == port
                && reply.DestAddress.Equals(target))
                match = reply;
            // else: stale or stray reply — drop.
        }

        if (match is null)
            return (false, null, null); // "*" — no answer
        var rtt = (long)((match.ReceivedTimestamp - sent) * 1000.0 / Stopwatch.Frequency);
        return (false, rtt, match);
    }

    private static long ElapsedMs(long sentTimestamp) =>
        (long)((Stopwatch.GetTimestamp() - sentTimestamp) * 1000.0 / Stopwatch.Frequency);

    private static string UnreachableNote(int code) => code switch
    {
        0 => "!N (net unreachable)",
        1 => "!H (host unreachable)",
        3 => "!P (port unreachable)",
        13 => "!X (admin prohibited)",
        _ => $"ICMP unreachable 3/{code}",
    };

    private static async Task<string?> TryResolveHostNameAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dnsCts.CancelAfter(1500);
            var entry = await Dns.GetHostEntryAsync(address, dnsCts.Token);
            var name = entry.HostName.TrimEnd('.');
            return string.IsNullOrEmpty(name) || name == address ? null : name;
        }
        catch
        {
            return null;
        }
    }
}
