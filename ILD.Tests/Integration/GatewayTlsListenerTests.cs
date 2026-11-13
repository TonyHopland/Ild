using System.Net;
using System.Net.Sockets;
using ILD.Api.Configuration;
using ILD.Core.Services.Implementations.ModelGateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace ILD.Tests.Integration;

public class GatewayTlsListenerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ild-gw-listener-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Only_the_gateway_port_is_served_with_the_gateway_certificate()
    {
        var plainPort = FreePort();
        var tls = GatewayTlsEndpoint.Create(FreePort(), _directory, (name, _) => Path.Combine(_directory, name));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{plainPort}", GatewayTlsListener.Url(tls, IPAddress.Loopback));
        builder.WebHost.ConfigureKestrel(kestrel => GatewayTlsListener.Configure(kestrel, tls));
        await using var app = builder.Build();
        app.MapGet("/", (HttpContext context) => context.Request.Scheme);
        await app.StartAsync(TestContext.Current.CancellationToken);

        string? presented = null;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                presented = certificate?.Thumbprint;
                return true;
            },
        };
        using var client = new HttpClient(handler);

        Assert.Equal("https", await client.GetStringAsync($"https://127.0.0.1:{tls.Port}/", TestContext.Current.CancellationToken));
        Assert.Equal(tls.Certificate.Thumbprint, presented);
        Assert.Equal("http", await client.GetStringAsync($"http://127.0.0.1:{plainPort}/", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("http://+:8081", "8080", "http://+:8081")]
    [InlineData(null, "8080", "http://*:8080")]
    [InlineData("", "8080;8081", "http://*:8080;http://*:8081")]
    [InlineData(null, null, "http://localhost:5000")]
    public void The_web_addresses_stay_what_Kestrel_would_have_listened_on(string? urls, string? httpPorts, string expected)
        => Assert.Equal(expected, GatewayTlsListener.WebUrls(urls, httpPorts));

    [Fact]
    public void The_listener_url_names_the_bind_address_and_the_gateway_port()
    {
        var tls = GatewayTlsEndpoint.Create(8442, _directory, (name, _) => Path.Combine(_directory, name));

        Assert.Equal("https://127.0.0.1:8442", GatewayTlsListener.Url(tls, IPAddress.Loopback));
        Assert.Equal("https://0.0.0.0:8442", GatewayTlsListener.Url(tls, IPAddress.Any));
    }
}
