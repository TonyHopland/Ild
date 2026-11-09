using System.Security.Cryptography.X509Certificates;
using ILD.Core.Services.Implementations.ModelGateway;

namespace ILD.Tests;

public class GatewayTlsEndpointTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ild-gw-tls-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private GatewayTlsEndpoint Create(params string[] hosts)
        => GatewayTlsEndpoint.Create(8442, _directory, (name, _) => Path.Combine(_directory, name), hosts);

    [Fact]
    public void The_certificate_covers_loopback_and_the_hosts_runs_reach_ild_by()
    {
        var tls = Create("ild.example", "ild-api", "10.1.2.3");

        foreach (var name in new[] { "127.0.0.1", "localhost", "ild.example", "ild-api", "10.1.2.3" })
            Assert.True(GatewayTlsEndpoint.Covers(tls.Certificate, name), name);
        Assert.False(GatewayTlsEndpoint.Covers(tls.Certificate, "other.example"));
    }

    [Fact]
    public void A_stored_certificate_that_covers_every_name_is_kept()
    {
        var first = Create("ild-api");
        var second = Create("ild-api");

        Assert.Equal(first.Certificate.Thumbprint, second.Certificate.Thumbprint);
    }

    [Fact]
    public void A_stored_certificate_missing_a_name_is_replaced()
    {
        var first = Create();
        var second = Create("ild-api");

        Assert.NotEqual(first.Certificate.Thumbprint, second.Certificate.Thumbprint);
        Assert.True(GatewayTlsEndpoint.Covers(second.Certificate, "ild-api"));
    }
}
