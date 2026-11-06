using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ILD.Tests.Integration;

public class ModelGatewayTests : IAsyncLifetime
{
    private const string RealKey = "sk-real-credential-0123456789";

    private readonly ApiFactory _factory = new();
    private readonly TaskCompletionSource _releaseSecondChunk = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication _upstream = null!;
    private string _upstreamUrl = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _upstream = builder.Build();
        _upstream.MapGet("/v1/echo", (HttpRequest request) => Results.Json(new
        {
            path = request.Path.Value,
            query = request.QueryString.Value,
            authorization = request.Headers.Authorization.ToString(),
            apiKey = request.Headers["x-api-key"].ToString(),
            gatewayToken = request.Headers["X-ILD-Gateway-Token"].ToString(),
        }));
        _upstream.MapPost("/v1/stream", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: first\n\n");
            await context.Response.Body.FlushAsync();
            await _releaseSecondChunk.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await context.Response.WriteAsync("data: second\n\n");
        });
        await _upstream.StartAsync(TestContext.Current.CancellationToken);
        _upstreamUrl = _upstream.Urls.First();
    }

    public async ValueTask DisposeAsync()
    {
        _releaseSecondChunk.TrySetResult();
        await _upstream.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private async Task<AiProvider> SeedProviderAsync()
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "gateway-test-" + Guid.NewGuid().ToString("N")[..6],
            Type = "opencode",
            BaseUrl = _upstreamUrl + "/v1",
            ApiKey = RealKey,
            Model = "m",
        };
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IProviderStore>().CreateAiProviderAsync(provider);
        return provider;
    }

    private ModelGatewayLease Open(AiProvider provider)
        => _factory.Services.GetRequiredService<IModelGatewayRegistry>()
            .Open(provider.Id, Guid.NewGuid(), "http://localhost", GatewayRoutes.ModelHosts(provider));

    private static HttpRequestMessage Get(string path, string? bearer = null, string? apiKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (apiKey is not null) request.Headers.Add("x-api-key", apiKey);
        return request;
    }

    [Fact]
    public async Task A_request_without_a_token_or_with_an_unknown_one_is_401()
    {
        var client = _factory.CreateClient();

        using var none = await client.SendAsync(Get("/gateway/upstream/echo"), TestContext.Current.CancellationToken);
        using var wrong = await client.SendAsync(Get("/gateway/upstream/echo", "ildgw_not-a-real-token"), TestContext.Current.CancellationToken);
        using var theAppsOwnToken = await client.SendAsync(
            Get("/gateway/upstream/echo", _factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, theAppsOwnToken.StatusCode);
    }

    [Fact]
    public async Task A_request_for_an_ended_run_is_403()
    {
        var provider = await SeedProviderAsync();
        var lease = Open(provider);
        lease.Dispose();

        using var response = await _factory.CreateClient()
            .SendAsync(Get("/gateway/upstream/echo", lease.Token), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_placeholder_bearer_is_replaced_by_the_real_credential_and_the_path_follows_the_provider_base_url()
    {
        var provider = await SeedProviderAsync();
        using var lease = Open(provider);

        using var response = await _factory.CreateClient()
            .SendAsync(Get("/gateway/upstream/echo?x=1", lease.Token), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("Bearer " + RealKey, seen.GetProperty("authorization").GetString());
        Assert.Equal("/v1/echo", seen.GetProperty("path").GetString());
        Assert.Equal("?x=1", seen.GetProperty("query").GetString());
        Assert.Equal("", seen.GetProperty("gatewayToken").GetString());
        Assert.DoesNotContain(lease.Token, seen.ToString());
    }

    [Fact]
    public async Task The_placeholder_x_api_key_is_replaced_in_the_same_header()
    {
        var provider = await SeedProviderAsync();
        using var lease = Open(provider);

        using var response = await _factory.CreateClient()
            .SendAsync(Get("/gateway/upstream/echo", apiKey: lease.Token), TestContext.Current.CancellationToken);

        var seen = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal(RealKey, seen.GetProperty("apiKey").GetString());
        Assert.Equal("", seen.GetProperty("authorization").GetString());
    }

    [Fact]
    public async Task A_token_in_the_gateway_header_authenticates_and_is_not_forwarded()
    {
        var provider = await SeedProviderAsync();
        using var lease = Open(provider);
        var request = Get("/gateway/upstream/echo", "static-placeholder");
        request.Headers.Add(ILD.Api.Middleware.ModelGatewayMiddleware.TokenHeader, lease.Token);

        using var response = await _factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("Bearer " + RealKey, seen.GetProperty("authorization").GetString());
        Assert.Equal("", seen.GetProperty("gatewayToken").GetString());
    }

    [Fact]
    public async Task A_provider_without_a_credential_is_not_forwarded()
    {
        var provider = await SeedProviderAsync();
        provider.ApiKey = null;
        await using (var scope = _factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IProviderStore>().UpdateAiProviderAsync(provider);
        using var lease = Open(provider);

        using var response = await _factory.CreateClient()
            .SendAsync(Get("/gateway/upstream/echo", lease.Token), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task A_streamed_answer_reaches_the_client_before_the_upstream_has_finished()
    {
        var provider = await SeedProviderAsync();
        using var lease = Open(provider);
        var request = new HttpRequestMessage(HttpMethod.Post, "/gateway/upstream/stream") { Content = new StringContent("{}") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.Token);

        using var response = await _factory.CreateClient()
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));

        var first = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("data: first", first);
        Assert.False(_releaseSecondChunk.Task.IsCompleted);

        _releaseSecondChunk.SetResult();
        await reader.ReadLineAsync(TestContext.Current.CancellationToken);
        Assert.Equal("data: second", await reader.ReadLineAsync(TestContext.Current.CancellationToken));
    }
}
