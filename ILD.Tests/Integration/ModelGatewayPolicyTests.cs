using System.Net;
using System.Net.Http.Headers;
using ILD.Api.Middleware;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// The model gateway judged by the real network policy and logged by the real recorder (ADR-0031, ADR-0019): the middleware, the
/// policy, the entries in the database and the network log are all real; only the model provider is a loopback listener.
/// </summary>
public class ModelGatewayPolicyTests : IAsyncLifetime
{
    private readonly StubSource _source = new();
    private readonly List<string> _hits = [];
    private WebApplication _upstream = null!;
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _host = null!;
    private int _port;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _upstream = builder.Build();
        _upstream.Run(context =>
        {
            lock (_hits) _hits.Add(context.Request.Path.Value!);
            return context.Response.WriteAsync("upstream");
        });
        await _upstream.StartAsync(TestContext.Current.CancellationToken);
        var url = new Uri(_upstream.Urls.First());
        (_host, _port) = (url.Host, url.Port);
        _source.BaseUrl = url.ToString();

        // The "private" protection is on, and the upstream is on loopback: the proxy would refuse both; the gateway checks no address.
        _factory = new ApiFactory(configureServices: services =>
        {
            services.AddSingleton<IModelCredentialSource>(_source);
            services.AddSingleton<IProtectedDestinationSource>(new PrivateLoopback());
        });
        _factory.UseKestrel(0);
        _factory.StartServer();
        _client = _factory.CreateDefaultClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _upstream.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private async Task SetModeAsync(string mode)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAppSettingStore>().UpsertAsync(AppSettingKeys.NetworkMode, mode);
        _factory.Services.GetRequiredService<IEgressPolicy>().Invalidate();
    }

    private async Task AddEntryAsync(NetworkListKind kind, Guid? providerId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INetworkPolicyStore>()
            .AddEntryAsync(new NetworkPolicyEntry { Host = _host, ListKind = kind, AiProviderId = providerId });
        _factory.Services.GetRequiredService<IEgressPolicy>().Invalidate();
    }

    private async Task<(Guid Provider, string Token)> OpenAsync()
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "gw-policy-" + Guid.NewGuid().ToString("N")[..6], Type = "opencode", BaseUrl = _source.BaseUrl, Model = "m" };
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IProviderStore>().CreateAiProviderAsync(provider);
        var lease = _factory.Services.GetRequiredService<IModelGatewayRegistry>()
            .Open(provider.Id, Guid.NewGuid(), "http://localhost", GatewayRoutes.ModelHosts(provider));
        return (provider.Id, lease.GitHubPlaceholder);
    }

    private async Task<(HttpResponseMessage Response, string Body)> CallAsync(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_client.BaseAddress!, "/gateway/upstream/chat/completions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task<NetworkLogEntry> LogRowAsync(Guid provider)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var rows = await scope.ServiceProvider.GetRequiredService<INetworkPolicyStore>().GetLogAsync(100, TestContext.Current.CancellationToken);
            if (rows.SingleOrDefault(r => r.AiProviderId == provider) is { } row) return row;
            Assert.True(DateTime.UtcNow < deadline, "the call was not logged");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private async Task AssertRowAsync(Guid provider, NetworkDecision decision)
    {
        var row = await LogRowAsync(provider);
        Assert.Equal(_host, row.Host);
        Assert.Equal(_port, row.Port);
        Assert.Equal(decision, row.Decision);
        Assert.Equal(ModelGatewayMiddleware.GatewayVia, row.Via);
        Assert.Null(row.Installer);
    }

    [Fact]
    public async Task The_proxy_refuses_both_a_LAN_address_and_the_loopback_upstream_used_here()
    {
        var snapshot = await _factory.Services.GetRequiredService<IEgressPolicy>().GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProtectedDestination.Private, snapshot.CheckAddresses([IPAddress.Parse("192.168.1.20")], isForward: false)!.ProtectedId);
        Assert.Equal(DestinationRefusal.LoopbackReason, snapshot.CheckAddresses([IPAddress.Loopback], isForward: false)!.Reason);
    }

    [Fact]
    public async Task Monitor_allows_the_call_and_logs_it_as_gateway_traffic()
    {
        await SetModeAsync("monitor");
        var (provider, token) = await OpenAsync();

        var (response, body) = await CallAsync(token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("upstream", body);
        await AssertRowAsync(provider, NetworkDecision.Advisory);
    }

    [Fact]
    public async Task Whitelist_refuses_an_upstream_that_is_not_listed_and_says_which_host_without_the_credential()
    {
        await SetModeAsync("whitelist");
        var (provider, token) = await OpenAsync();

        var (response, body) = await CallAsync(token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains($"ILD's network policy refused {_host}", body);
        Assert.DoesNotContain("real", body);
        Assert.DoesNotContain("upstream\"", body);
        Assert.Empty(_hits);
        await AssertRowAsync(provider, NetworkDecision.Blocked);
    }

    [Fact]
    public async Task Whitelist_allows_the_upstream_with_an_entry_scoped_to_the_provider_and_not_one_scoped_to_another()
    {
        await SetModeAsync("whitelist");
        var (provider, token) = await OpenAsync();
        var (otherProvider, otherToken) = await OpenAsync();
        await AddEntryAsync(NetworkListKind.Whitelist, provider);

        var (allowed, _) = await CallAsync(token);
        var (refused, _) = await CallAsync(otherToken);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        await AssertRowAsync(provider, NetworkDecision.Allowed);
        await AssertRowAsync(otherProvider, NetworkDecision.Blocked);
        Assert.Single(_hits);
    }

    [Fact]
    public async Task Blacklist_refuses_a_listed_upstream_and_allows_an_unlisted_one()
    {
        await SetModeAsync("blacklist");
        var (provider, token) = await OpenAsync();
        var (allowed, _) = await CallAsync(token);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        await AssertRowAsync(provider, NetworkDecision.Allowed);

        await AddEntryAsync(NetworkListKind.Blacklist, null);
        var (second, secondToken) = await OpenAsync();
        var (refused, body) = await CallAsync(secondToken);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains(_host, body);
        await AssertRowAsync(second, NetworkDecision.Blocked);
        Assert.Single(_hits);
    }

    [Fact]
    public async Task A_provider_with_its_whitelist_entry_from_creation_keeps_working_in_whitelist_mode()
    {
        await SetModeAsync("whitelist");
        var (provider, token) = await OpenAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await new ProviderNetworkEntries(db, _factory.Services.GetRequiredService<IEgressPolicy>())
                .SyncAsync((await db.AiProviders.FindAsync([provider], TestContext.Current.CancellationToken))!, TestContext.Current.CancellationToken);
        }

        var (response, _) = await CallAsync(token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertRowAsync(provider, NetworkDecision.Allowed);
    }

    private sealed class PrivateLoopback : IProtectedDestinationSource
    {
        public ValueTask<IReadOnlyList<ProtectedDestination>> GetAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ProtectedDestination>>(
                [ProtectedDestination.PrivateRanges]);
    }

    private sealed class StubSource : IModelCredentialSource
    {
        public string BaseUrl { get; set; } = "";

        public Task<ModelCredential?> ResolveAsync(AiProvider provider, string route, CancellationToken ct)
            => Task.FromResult<ModelCredential?>(new ModelCredential(new Uri(BaseUrl), "real", ModelCredential.NoHeaders));

        public void Rejected(AiProvider provider, string route) { }
    }
}
