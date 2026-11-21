using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using FluentResults;

namespace WindowsUtils.Core.Net;

/// <summary>One certificate of the chain the server sent, leaf first.</summary>
public sealed record TlsChainCertificate(
    string Subject,
    string Issuer,
    DateTime NotBefore,
    DateTime NotAfter,
    string Thumbprint,
    string Status);

/// <summary>What a TLS handshake with a server negotiated, and the certificate it presented.</summary>
public sealed record TlsProbeResult(
    string Host,
    int Port,
    string RemoteAddress,
    string Protocol,
    string CipherSuite,
    string Subject,
    string Issuer,
    IReadOnlyList<string> DnsNames,
    DateTime NotBefore,
    DateTime NotAfter,
    string ValidationErrors,
    IReadOnlyList<TlsChainCertificate> Chain)
{
    /// <summary>Whole days until the certificate expires (negative once it has).</summary>
    public int DaysLeft => (int)Math.Floor((NotAfter - DateTime.Now).TotalDays);
}

/// <summary>
/// Connects to host:port and does a TLS client handshake with the operating system's protocol
/// defaults (Schannel), then reports the negotiated version, cipher suite and the certificate chain.
/// Only the handshake runs: no application data is sent, and the certificate is inspected even when
/// it is invalid (its problems are reported in <see cref="TlsProbeResult.ValidationErrors"/>).
/// Revocation is not checked, so no request goes to the CA's CRL/OCSP servers.
/// </summary>
public static class TlsProbe
{
    public const int MaxHostLength = 253;

    /// <summary>Checks the host and port a user or the model typed: a DNS name or an IP address, 1-65535.</summary>
    public static Result<(string Host, int Port)> Validate(string? host, int port)
    {
        var h = (host ?? "").Trim();
        if (h.Length == 0 || h.Length > MaxHostLength)
            return Result.Fail("Enter a host name or IP address.");
        // Keeps URLs, user:pass@ and paths out: only a bare name or address is accepted.
        if (Uri.CheckHostName(h.Trim('[', ']')) == UriHostNameType.Unknown)
            return Result.Fail($"Not a valid host name or IP address: {h}");
        if (port is < 1 or > 65535)
            return Result.Fail("The port must be between 1 and 65535.");
        return (h.Trim('[', ']'), port);
    }

    /// <summary>Probes host:port. Expected network failures (no connection, timeout, failed handshake)
    /// are failed results; cancellation through <paramref name="cancellationToken"/> propagates.</summary>
    public static Task<Result<TlsProbeResult>> ProbeAsync(
        string host, int port, int timeoutMs = 10000, CancellationToken cancellationToken = default) =>
        Validate(host, port).Bind(v =>
            HandshakeAsync(v.Host, v.Port, Math.Clamp(timeoutMs, 1000, 60000), cancellationToken));

    private static async Task<Result<TlsProbeResult>> HandshakeAsync(
        string host, int port, int timeoutMs, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        var endpoint = $"{host}:{port}";

        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result.Fail($"No connection to {endpoint} within {timeoutMs / 1000} s.");
        }
        catch (SocketException ex)
        {
            return Result.Fail(new ExceptionalError($"Could not connect to {endpoint}: {ex.Message}", ex));
        }

        var remote = client.Client.RemoteEndPoint is IPEndPoint ip
            ? (ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address).ToString()
            : "";

        // The callback runs during the handshake; the chain is only valid inside it, so copy what we show.
        CertificateCapture? capture = null;
        await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.None, // the operating system's defaults (Schannel settings)
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
            {
                capture = CertificateCapture.From(certificate, chain, errors);
                return true; // inspect only: nothing is sent after the handshake
            },
        };

        try
        {
            await ssl.AuthenticateAsClientAsync(options, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result.Fail($"The TLS handshake with {endpoint} did not finish within {timeoutMs / 1000} s.");
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException)
        {
            return Result.Fail(new ExceptionalError(
                $"TLS handshake with {endpoint} failed: {ex.GetBaseException().Message}", ex));
        }

        if (capture is null)
            return Result.Fail($"{endpoint} did not send a certificate.");

        return new TlsProbeResult(
            host, port, remote,
            DescribeProtocol(ssl.SslProtocol),
            ssl.NegotiatedCipherSuite.ToString(),
            capture.Subject, capture.Issuer, capture.DnsNames,
            capture.NotBefore, capture.NotAfter, capture.Errors, capture.Chain);
    }

    private static string DescribeProtocol(SslProtocols protocol) => protocol switch
    {
#pragma warning disable CS0618, SYSLIB0039 // named only to describe what was negotiated
        SslProtocols.Ssl3 => "SSL 3.0",
        SslProtocols.Tls => "TLS 1.0",
        SslProtocols.Tls11 => "TLS 1.1",
#pragma warning restore CS0618, SYSLIB0039
        SslProtocols.Tls12 => "TLS 1.2",
        SslProtocols.Tls13 => "TLS 1.3",
        _ => protocol.ToString(),
    };

    private sealed record CertificateCapture(
        string Subject, string Issuer, IReadOnlyList<string> DnsNames,
        DateTime NotBefore, DateTime NotAfter, string Errors, IReadOnlyList<TlsChainCertificate> Chain)
    {
        public static CertificateCapture? From(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (certificate is null)
                return null;
            var leaf = certificate as X509Certificate2 ?? new X509Certificate2(certificate);

            var elements = chain?.ChainElements.Select(e => new TlsChainCertificate(
                e.Certificate.Subject,
                e.Certificate.Issuer,
                e.Certificate.NotBefore,
                e.Certificate.NotAfter,
                e.Certificate.Thumbprint,
                e.ChainElementStatus.Length == 0
                    ? "OK"
                    : string.Join(", ", e.ChainElementStatus.Select(s => s.Status.ToString())))).ToList() ?? [];

            return new CertificateCapture(
                leaf.Subject,
                leaf.Issuer,
                DnsNamesOf(leaf),
                leaf.NotBefore,
                leaf.NotAfter,
                errors == SslPolicyErrors.None ? "None" : errors.ToString(),
                elements);
        }

        private static IReadOnlyList<string> DnsNamesOf(X509Certificate2 certificate)
        {
            var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
            return san is null ? [] : [.. san.EnumerateDnsNames()];
        }
    }
}
