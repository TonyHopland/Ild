using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

public class CopilotGatewayTests : IAsyncLifetime
{
    private const string UserJson = """
        {"login":"someone","endpoints":{"api":"https://api.individual.githubcopilot.com","telemetry":"https://telemetry.individual.githubcopilot.com"},"note":"https://github.com/stays"}
        """;

    private WebApplication _upstream = null!;
    private ApiFactory _factory = null!;
    private string? _seenAuthorization;
    private string? _seenAcceptEncoding;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _upstream = builder.Build();
        _upstream.MapGet("/copilot_internal/user", (HttpRequest request) =>
        {
            _seenAuthorization = request.Headers.Authorization.ToString();
            _seenAcceptEncoding = request.Headers.AcceptEncoding.ToString();
            return Results.Text(UserJson, "application/json");
        });
        await _upstream.StartAsync(TestContext.Current.CancellationToken);
        var upstreamUrl = _upstream.Urls.First();

        var certificates = Directory.CreateTempSubdirectory("ild-gw-tls-").FullName;
        var tls = GatewayTlsEndpoint.Create(8442, certificates, (name, _) => Path.Combine(certificates, name));
        _factory = new ApiFactory(configureServices: services =>
        {
            services.AddSingleton(tls);
            services.AddSingleton<IModelCredentialSource>(new StubSource(new Uri(upstreamUrl)));
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _upstream.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private async Task<ModelGatewayLeaseHolder> OpenAsync()
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "copilot-gw", Type = "copilot", BaseUrl = "", Model = "" };
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IProviderStore>().CreateAiProviderAsync(provider);
        var lease = _factory.Services.GetRequiredService<IModelGatewayRegistry>()
            .Open(provider.Id, Guid.NewGuid(), "http://localhost", GatewayRoutes.ModelHosts(provider));
        return new ModelGatewayLeaseHolder(lease);
    }

    private sealed class ModelGatewayLeaseHolder(ILD.Data.DTOs.ModelGatewayLease lease) : IDisposable
    {
        public string GitHubToken => lease.GitHubPlaceholder;
        public void Dispose() => lease.Dispose();
    }

    private HttpRequestMessage Request(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        return request;
    }

    [Fact]
    public async Task The_user_answer_has_its_copilot_endpoints_rewritten_to_the_gateway_and_the_token_swapped()
    {
        using var lease = await OpenAsync();

        using var response = await _factory.CreateClient()
            .SendAsync(Request("/gateway/github/copilot_internal/user", lease.GitHubToken), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("https://127.0.0.1:8442/_ep/api.individual.githubcopilot.com", body.GetProperty("endpoints").GetProperty("api").GetString());
        Assert.Equal("https://127.0.0.1:8442/_ep/telemetry.individual.githubcopilot.com", body.GetProperty("endpoints").GetProperty("telemetry").GetString());
        Assert.Equal("https://github.com/stays", body.GetProperty("note").GetString());
        Assert.Equal("Bearer gho_real", _seenAuthorization);
        Assert.Equal("", _seenAcceptEncoding);
    }

    [Fact]
    public async Task Only_copilot_hosts_are_routed_through_the_endpoint_prefix()
    {
        using var lease = await OpenAsync();
        var client = _factory.CreateClient();

        using var foreign = await client.SendAsync(Request("/gateway/github/_ep/attacker.example.test/models", lease.GitHubToken), TestContext.Current.CancellationToken);
        using var suffixTrick = await client.SendAsync(Request("/gateway/github/_ep/api.githubcopilot.com.example.test/models", lease.GitHubToken), TestContext.Current.CancellationToken);
        using var otherGitHubApi = await client.SendAsync(Request("/gateway/github/user/repos", lease.GitHubToken), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, suffixTrick.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherGitHubApi.StatusCode);
    }

    private sealed class StubSource(Uri upstream) : IModelCredentialSource
    {
        public Task<ModelCredential?> ResolveAsync(AiProvider provider, string route, CancellationToken ct)
            => Task.FromResult<ModelCredential?>(new ModelCredential(upstream, "gho_real", ModelCredential.NoHeaders));

        public void Rejected(AiProvider provider, string route)
        {
        }
    }
}
