using System.Net;
using System.Net.Http.Headers;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// The gateway's path rules, driven through real Kestrel: TestServer hands the middleware a path that has
/// not been through Kestrel's decoding, which is how an encoded-dot escape got past the first tests.
/// </summary>
public class ModelGatewayPathTests : IAsyncLifetime
{
    private readonly StubSource _source = new();
    private readonly List<string> _hits = [];
    private WebApplication _upstream = null!;
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

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
        _source.BaseUrl = _upstream.Urls.First();

        _factory = new ApiFactory(configureServices: services => services.AddSingleton<IModelCredentialSource>(_source));
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

    private async Task<string> OpenAsync(string type)
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "gw-path-" + Guid.NewGuid().ToString("N")[..6], Type = type, BaseUrl = "", Model = "" };
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IProviderStore>().CreateAiProviderAsync(provider);
        var lease = _factory.Services.GetRequiredService<IModelGatewayRegistry>()
            .Open(provider.Id, Guid.NewGuid(), "http://localhost", GatewayRoutes.ModelHosts(provider));
        return lease.GitHubPlaceholder;
    }

    private async Task<HttpResponseMessage> SendAsync(string type, string pathAndQuery)
    {
        var token = await OpenAsync(type);
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_client.BaseAddress!, pathAndQuery));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("copilot", "/gateway/github/copilot_internal/%252e%252e/user/repos", "")]
    [InlineData("copilot", "/gateway/github/copilot_internal/.%252e/user/repos", "")]
    [InlineData("copilot", "/gateway/github/copilot_internal/v2/token", "")]
    [InlineData("copilot", "/gateway/github/user/repos", "")]
    [InlineData("copilot", "/gateway/github/mcp/readonly", "")]
    [InlineData("copilot", "/gateway/github/_ep", "")]
    [InlineData("chatgpt", "/gateway/chatgpt/codex/%252e%252e/conversations", "/backend-api")]
    [InlineData("chatgpt", "/gateway/chatgpt/wham/.%252e/conversations", "/backend-api")]
    [InlineData("chatgpt", "/gateway/chatgpt/conversations", "/backend-api")]
    [InlineData("chatgpt", "/gateway/chatgpt/wham/tasks", "/backend-api")]
    [InlineData("chatgpt", "/gateway/chatgpt/wham/tasks/x", "/backend-api")]
    [InlineData("chatgpt", "/gateway/chatgpt/wham/environments", "/backend-api")]
    [InlineData("chatgpt", "/gateway/chatgpt/wham/usage/x", "/backend-api")]
    [InlineData("claude", "/gateway/anthropic/v1/messages/%252e%252e/%252e%252e/api/oauth/profile", "")]
    [InlineData("claude", "/gateway/anthropic/v1/models/.%252e/messages", "")]
    [InlineData("claude", "/gateway/anthropic/api/oauth/profile", "")]
    [InlineData("opencode", "/gateway/upstream/models/%252e%252e/admin", "/v1")]
    [InlineData("opencode", "/gateway/upstream/models/.%252e/%252e%252e/admin", "/v1")]
    [InlineData("opencode", "/gateway/upstream/admin", "/v1")]
    [InlineData("opencode", "/gateway/upstream/v1/v1/messages", "")]
    [InlineData("opencode", "/gateway/upstream/v1/admin", "")]
    public async Task A_path_that_escapes_or_is_not_allowed_is_refused_without_contacting_the_upstream(string type, string path, string basePath)
    {
        _source.BaseUrl = _upstream.Urls.First() + basePath;

        using var response = await SendAsync(type, path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_hits);
    }

    [Theory]
    [InlineData("copilot", "/gateway/github/copilot_internal/user", "", "/copilot_internal/user")]
    [InlineData("chatgpt", "/gateway/chatgpt/codex/models?client_version=1", "/backend-api", "/backend-api/codex/models")]
    [InlineData("chatgpt", "/gateway/chatgpt/codex/responses", "/backend-api", "/backend-api/codex/responses")]
    [InlineData("chatgpt", "/gateway/chatgpt/wham/usage", "/backend-api", "/backend-api/wham/usage")]
    [InlineData("claude", "/gateway/anthropic/v1/messages", "", "/v1/messages")]
    [InlineData("claude", "/gateway/anthropic/v1/messages/count_tokens", "", "/v1/messages/count_tokens")]
    [InlineData("opencode", "/gateway/upstream/chat/completions", "/v1", "/v1/chat/completions")]
    [InlineData("opencode", "/gateway/upstream/v1/messages", "", "/v1/messages")]
    [InlineData("opencode", "/gateway/upstream/v1/chat/completions", "", "/v1/chat/completions")]
    [InlineData("opencode", "/gateway/upstream/v1beta/models/gemini:streamGenerateContent", "", "/v1beta/models/gemini:streamGenerateContent")]
    public async Task An_allowed_path_on_each_route_still_reaches_the_upstream(string type, string path, string basePath, string upstreamPath)
    {
        _source.BaseUrl = _upstream.Urls.First() + basePath;

        using var response = await SendAsync(type, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([upstreamPath], _hits);
    }

    private sealed class StubSource : IModelCredentialSource
    {
        public string BaseUrl { get; set; } = "";

        public Task<ModelCredential?> ResolveAsync(AiProvider provider, string route, CancellationToken ct)
            => Task.FromResult<ModelCredential?>(new ModelCredential(new Uri(BaseUrl), "real", ModelCredential.NoHeaders));

        public void Rejected(AiProvider provider, string route) { }
    }
}
