using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ILD.Core.Services.Implementations.ModelGateway;

/// <summary>
/// The loopback HTTPS listener the Copilot CLI needs: it takes only an HTTPS host for its GitHub
/// endpoint. The certificate is ILD's own server certificate, made once and kept in the
/// orchestrator-private data root; the agent is given its public half to trust, not a CA, so
/// nothing is intercepted (ADR-0031).
/// </summary>
public sealed class GatewayTlsEndpoint
{
    public const string PortVariable = "ILD_GATEWAY_TLS_PORT";
    public const int DefaultPort = 8442;
    private static readonly TimeSpan RenewWithin = TimeSpan.FromDays(30);

    public GatewayTlsEndpoint(int port, X509Certificate2 certificate, string caFile)
    {
        Port = port;
        Certificate = certificate;
        CaFile = caFile;
    }

    public int Port { get; }
    public X509Certificate2 Certificate { get; }

    /// <summary>The certificate in PEM form, where the agent can read it, for <c>NODE_EXTRA_CA_CERTS</c>.</summary>
    public string CaFile { get; }

    /// <summary>The configured port, or null when the listener is switched off with port 0.</summary>
    public static int? PortFrom(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? DefaultPort : int.TryParse(configured, out var port) && port > 0 ? port : null;

    /// <param name="hostNames">The names runs reach ILD by, which the certificate must cover besides loopback; a stored certificate that lacks one is replaced.</param>
    public static GatewayTlsEndpoint Create(int port, string privateDirectory, Func<string, byte[], string> publish, IEnumerable<string>? hostNames = null)
    {
        Directory.CreateDirectory(privateDirectory);
        var pfx = Path.Combine(privateDirectory, "gateway-tls.pfx");
        var names = (hostNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim().Trim('[', ']').ToLowerInvariant()).Distinct().ToList();
        var certificate = Load(pfx, names) ?? Generate(pfx, names);
        var caFile = publish("gateway-tls.pem", System.Text.Encoding.ASCII.GetBytes(certificate.ExportCertificatePem()));
        return new GatewayTlsEndpoint(port, certificate, caFile);
    }

    private static X509Certificate2? Load(string pfx, IReadOnlyList<string> hostNames)
    {
        if (!File.Exists(pfx)) return null;
        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(pfx, null);
            return certificate.NotAfter - DateTime.Now > RenewWithin && hostNames.All(name => Covers(certificate, name)) ? certificate : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Whether the certificate's subject alternative names include <paramref name="name"/>, a DNS name or an IP address.</summary>
    public static bool Covers(X509Certificate2 certificate, string name)
    {
        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null) return false;
        return IPAddress.TryParse(name, out var ip)
            ? san.EnumerateIPAddresses().Contains(ip)
            : san.EnumerateDnsNames().Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static X509Certificate2 Generate(string pfx, IReadOnlyList<string> hostNames)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=ILD model gateway", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        foreach (var host in hostNames)
        {
            if (host == "localhost" || host == IPAddress.Loopback.ToString()) continue;
            if (IPAddress.TryParse(host, out var ip)) names.AddIpAddress(ip);
            else names.AddDnsName(host);
        }
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        var bytes = created.Export(X509ContentType.Pkcs12);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(pfx, options))
            stream.Write(bytes);
        return X509CertificateLoader.LoadPkcs12(bytes, null);
    }
}
