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
        _upstream.MapGet("/v1/models", (HttpRequest request) => Results.Json(new
        {
            path = request.Path.Value,
            query = request.QueryString.Value,
            authorization = request.Headers.Authorization.ToString(),
            apiKey = request.Headers["x-api-key"].ToString(),
            gatewayToken = request.Headers["X-ILD-Gateway-Token"].ToString(),
        }));
        _upstream.MapPost("/v1/responses", async (HttpContext context) =>
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

        using var none = await client.SendAsync(Get("/gateway/upstream/models"), TestContext.Current.CancellationToken);
        using var wrong = await client.SendAsync(Get("/gateway/upstream/models", "ildgw_not-a-real-token"), TestContext.Current.CancellationToken);
        using var theAppsOwnToken = await client.SendAsync(
            Get("/gateway/upstream/models", _factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token),
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
            .SendAsync(Get("/gateway/upstream/models", lease.Token), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_placeholder_bearer_is_replaced_by_the_real_credential_and_the_path_follows_the_provider_base_url()
    {
        var provider = await SeedProviderAsync();
        using var lease = Open(provider);

        using var response = await _factory.CreateClient()
            .SendAsync(Get("/gateway/upstream/models?x=1", lease.Token), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("Bearer " + RealKey, seen.GetProperty("authorization").GetString());
        Assert.Equal("/v1/models", seen.GetProperty("path").GetString());
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
            .SendAsync(Get("/gateway/upstream/models", apiKey: lease.Token), TestContext.Current.CancellationToken);

        var seen = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal(RealKey, seen.GetProperty("apiKey").GetString());
        Assert.Equal("", seen.GetProperty("authorization").GetString());
    }

    [Fact]
    public async Task A_token_in_the_gateway_header_authenticates_and_is_not_forwarded()
    {
        var provider = await SeedProviderAsync();
        using var lease = Open(provider);
        var request = Get("/gateway/upstream/models", "static-placeholder");
        request.Headers.Add(ILD.Api.Middleware.ModelGatewayMiddleware.TokenHeader, lease.Token);

        using var response = await _factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("Bearer " + RealKey, seen.GetProperty("authorization").GetString());
        Assert.Equal("", seen.GetProperty("gatewayToken").GetString());
    }

    [Theory]
    [InlineData("/gateway/upstream//evil.example/v1/models")]
    [InlineData("/gateway/upstream/%2Fevil.example/v1/models")]
    [InlineData("/gateway/upstream/..%2F..%2Fx")]
    [InlineData("/gateway/upstream/a%5Cb")]
    [InlineData("/gateway/upstream/a%23b")]
    [InlineData("/gateway/upstream/a%3Fb")]
    [InlineData("/gateway/upstream/a%40evil.example")]
    public async Task A_path_that_tries_to_choose_the_upstream_host_is_refused_and_nothing_is_sent(string path)
    {
        var provider = await SeedProviderAsync();
        provider.BaseUrl = _upstreamUrl;
        await using (var scope = _factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IProviderStore>().UpdateAiProviderAsync(provider);
        using var lease = Open(provider);

        using var response = await _factory.CreateClient().SendAsync(Get(path, lease.Token), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/gateway/github/_ep/evil.example%23.githubcopilot.com/x")]
    [InlineData("/gateway/github/_ep/evil.example%3F.githubcopilot.com/x")]
    [InlineData("/gateway/github/_ep/a@evil.example%23.githubcopilot.com/x")]
    [InlineData("/gateway/github/_ep/%2Fevil.example/x")]
    public void A_copilot_endpoint_host_must_be_a_plain_githubcopilot_host(string path)
    {
        var rest = Uri.UnescapeDataString(path["/gateway/github/".Length..]);

        Assert.False(ILD.Api.Middleware.ModelGatewayMiddleware.TryBuildTarget(
            new Uri("https://api.github.com"), GatewayRoutes.GitHub, rest, default, out _, out _));
    }

    [Theory]
    [InlineData(GatewayRoutes.Anthropic, "v1/messagesX")]
    [InlineData(GatewayRoutes.Anthropic, "v1/messages/other")]
    [InlineData(GatewayRoutes.ChatGpt, "codexfoo")]
    [InlineData(GatewayRoutes.ChatGpt, "codex")]
    [InlineData(GatewayRoutes.Upstream, "modelsX")]
    [InlineData(GatewayRoutes.Upstream, "models/%2e%2e/x")]
    [InlineData(GatewayRoutes.Upstream, "models/./x")]
    public void A_path_outside_the_routes_allowlist_is_refused(string route, string rest)
    {
        Assert.False(ILD.Api.Middleware.ModelGatewayMiddleware.TryBuildTarget(
            new Uri("https://example.test/base"), route, rest, default, out _, out _));
    }

    [Fact]
    public void A_plain_copilot_endpoint_keeps_its_host_path_and_query()
    {
        Assert.True(ILD.Api.Middleware.ModelGatewayMiddleware.TryBuildTarget(
            new Uri("https://api.github.com"), GatewayRoutes.GitHub, "_ep/api.individual.githubcopilot.com/v1/messages",
            new QueryString("?a=1"), out var target, out var withCredential));

        Assert.Equal("https://api.individual.githubcopilot.com/v1/messages?a=1", target.ToString());
        Assert.True(withCredential);
    }

    [Theory]
    [InlineData("_ep/api.individual.githubcopilot.com/mcp/readonly")]
    [InlineData("_ep/api.githubcopilot.com/mcp/readonly")]
    [InlineData("_ep/api.individual.githubcopilot.com/user/repos")]
    [InlineData("_ep/api.individual.githubcopilot.com/v1/messagesX")]
    [InlineData("_ep/api.individual.githubcopilot.com/")]
    [InlineData("_ep/exp.individual.githubcopilot.com/experiments")]
    [InlineData("_ep/origin-tracker.individual.githubcopilot.com/origin")]
    [InlineData("_ep/proxy.individual.githubcopilot.com/v1/messages")]
    [InlineData("_ep/telemetry.individual.githubcopilot.com/mcp/readonly")]
    [InlineData("_ep/telemetry.individual.githubcopilot.com/v1/messages")]
    [InlineData("mcp/readonly")]
    public void A_copilot_path_outside_its_hosts_allowlist_is_refused(string rest)
    {
        Assert.False(ILD.Api.Middleware.ModelGatewayMiddleware.TryBuildTarget(
            new Uri("https://api.github.com"), GatewayRoutes.GitHub, rest, default, out _, out _));
    }

    [Theory]
    [InlineData("_ep/api.individual.githubcopilot.com/models", "https://api.individual.githubcopilot.com/models")]
    [InlineData("_ep/api.individual.githubcopilot.com/models/claude-sonnet-4/policy", "https://api.individual.githubcopilot.com/models/claude-sonnet-4/policy")]
    [InlineData("_ep/api.individual.githubcopilot.com/v1/messages", "https://api.individual.githubcopilot.com/v1/messages")]
    [InlineData("_ep/api.individual.githubcopilot.com/v1/messages/count_tokens", "https://api.individual.githubcopilot.com/v1/messages/count_tokens")]
    [InlineData("_ep/api.individual.githubcopilot.com/chat/completions", "https://api.individual.githubcopilot.com/chat/completions")]
    [InlineData("_ep/api.business.githubcopilot.com/responses", "https://api.business.githubcopilot.com/responses")]
    [InlineData("_ep/api.githubcopilot.com/chat/completions", "https://api.githubcopilot.com/chat/completions")]
    public void A_copilot_model_endpoint_is_forwarded_with_the_credential(string rest, string expected)
    {
        Assert.True(ILD.Api.Middleware.ModelGatewayMiddleware.TryBuildTarget(
            new Uri("https://api.github.com"), GatewayRoutes.GitHub, rest, default, out var target, out var withCredential));

        Assert.Equal(expected, target.ToString());
        Assert.True(withCredential);
    }

    [Fact]
    public void Copilot_telemetry_is_forwarded_without_a_credential()
    {
        Assert.True(ILD.Api.Middleware.ModelGatewayMiddleware.TryBuildTarget(
            new Uri("https://api.github.com"), GatewayRoutes.GitHub, "_ep/telemetry.individual.githubcopilot.com/telemetry",
            default, out var target, out var withCredential));

        Assert.Equal("https://telemetry.individual.githubcopilot.com/telemetry", target.ToString());
        Assert.False(withCredential);
    }

    [Fact]
    public async Task A_request_forwarded_without_a_credential_carries_none_of_the_agents_credential_headers()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Headers.Authorization = "Bearer gho_placeholder";
        context.Request.Headers["x-api-key"] = "placeholder";
        context.Request.Headers["api-key"] = "placeholder";
        context.Request.Headers[GatewayRoutes.TokenHeader] = "placeholder";
        context.Request.Headers["x-copilot-client"] = "kept";
        var target = new Uri("https://telemetry.individual.githubcopilot.com/telemetry");
        using var proxyRequest = new HttpRequestMessage(HttpMethod.Post, target);

        await new ILD.Api.Middleware.ModelGatewayMiddleware.SwapTransformer(target, credential: null, rewriteEndpointsTo: null)
            .TransformRequestAsync(context, proxyRequest, "https://telemetry.individual.githubcopilot.com", TestContext.Current.CancellationToken);

        Assert.Null(proxyRequest.Headers.Authorization);
        foreach (var name in new[] { "x-api-key", "api-key", GatewayRoutes.TokenHeader })
            Assert.False(proxyRequest.Headers.Contains(name), name);
        Assert.Equal("kept", Assert.Single(proxyRequest.Headers.GetValues("x-copilot-client")));
        Assert.Equal(target, proxyRequest.RequestUri);
    }

    [Fact]
    public async Task A_request_forwarded_with_a_credential_carries_the_real_one()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Headers.Authorization = "Bearer gho_placeholder";
        var target = new Uri("https://api.individual.githubcopilot.com/v1/messages");
        using var proxyRequest = new HttpRequestMessage(HttpMethod.Post, target);
        var credential = new ModelCredential(new Uri("https://api.github.com"), RealKey, ModelCredential.NoHeaders);

        await new ILD.Api.Middleware.ModelGatewayMiddleware.SwapTransformer(target, credential, rewriteEndpointsTo: null)
            .TransformRequestAsync(context, proxyRequest, "https://api.individual.githubcopilot.com", TestContext.Current.CancellationToken);

        Assert.Equal("Bearer " + RealKey, proxyRequest.Headers.Authorization?.ToString());
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
            .SendAsync(Get("/gateway/upstream/models", lease.Token), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task A_streamed_answer_reaches_the_client_before_the_upstream_has_finished()
    {
        var provider = await SeedProviderAsync();
        using var lease = Open(provider);
        var request = new HttpRequestMessage(HttpMethod.Post, "/gateway/upstream/responses") { Content = new StringContent("{}") };
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
