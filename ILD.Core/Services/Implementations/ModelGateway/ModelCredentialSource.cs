using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Core.Services.Implementations.ModelGateway;

/// <summary>Where the gateway sends a request and the real credential it carries.</summary>
public sealed record ModelCredential(Uri Upstream, string Secret, IReadOnlyDictionary<string, string> Headers)
{
    public static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();
}

/// <summary>Finds the real credential for a provider. Only the orchestrator can read what this reads (ADR-0031).</summary>
public interface IModelCredentialSource
{
    /// <returns>The credential, or null when the provider has none ILD can use.</returns>
    Task<ModelCredential?> ResolveAsync(AiProvider provider, string route, CancellationToken ct);

    /// <summary>The upstream refused the credential last handed out for the route, so the next resolve should renew it first if it can.</summary>
    void Rejected(AiProvider provider, string route);
}

public sealed class ModelCredentialSource : IModelCredentialSource
{
    private static readonly Uri AnthropicUpstream = new($"https://{GatewayRoutes.AnthropicHost}");
    private static readonly Uri GitHubUpstream = new($"https://{GatewayRoutes.GitHubApiHost}");
    private static readonly Uri ChatGptUpstream = new($"https://{GatewayRoutes.ChatGptHost}/backend-api");

    private readonly IProcessEnvironment _environment;
    private readonly CodexLoginStore _codex;
    private readonly TimeProvider _clock;

    public ModelCredentialSource(
        HttpClient http, ILogger<ModelCredentialSource>? logger = null, IProcessEnvironment? environment = null, TimeProvider? clock = null)
    {
        _environment = environment ?? ProcessEnvironment.Current;
        _clock = clock ?? TimeProvider.System;
        _codex = new CodexLoginStore(
            Path.Combine(HomeDirectory(), ".codex", "auth.json"), http, _clock, logger ?? NullLogger<ModelCredentialSource>.Instance);
    }

    public async Task<ModelCredential?> ResolveAsync(AiProvider provider, string route, CancellationToken ct)
    {
        if (GatewayRoutes.RouteFor(provider.Type) != route) return null;
        switch (route)
        {
            case GatewayRoutes.Anthropic:
            {
                var secret = ClaudeLongLivedToken(provider) ?? ClaudeStoredAccessToken();
                return secret is null ? null : new ModelCredential(AnthropicUpstream, secret, ModelCredential.NoHeaders);
            }
            case GatewayRoutes.ChatGpt:
            {
                var login = await _codex.CurrentAsync(ct);
                if (login is null) return null;
                var headers = login.AccountId is null
                    ? ModelCredential.NoHeaders
                    : new Dictionary<string, string> { ["ChatGPT-Account-ID"] = login.AccountId };
                return new ModelCredential(ChatGptUpstream, login.AccessToken, headers);
            }
            case GatewayRoutes.GitHub:
            {
                var secret = CopilotToken();
                return secret is null ? null : new ModelCredential(GitHubUpstream, secret, ModelCredential.NoHeaders);
            }
            case GatewayRoutes.Upstream:
            {
                var key = UpstreamKey(provider);
                return key is null || !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var upstream)
                    ? null
                    : new ModelCredential(upstream, key, ModelCredential.NoHeaders);
            }
            default:
                return null;
        }
    }

    public void Rejected(AiProvider provider, string route)
    {
        if (route == GatewayRoutes.ChatGpt) _codex.RefreshNext();
    }

    /// <summary>The API key or <c>claude setup-token</c> token saved on the provider.</summary>
    public static string? ClaudeLongLivedToken(AiProvider provider)
        => string.IsNullOrWhiteSpace(provider.ApiKey) ? null : provider.ApiKey.Trim();

    /// <summary>The key a base-URL provider (OpenCode, Pi) calls its endpoint with.</summary>
    public static string? UpstreamKey(AiProvider provider)
    {
        var key = !string.IsNullOrWhiteSpace(provider.ApiKey) ? provider.ApiKey : AiProviderConfig.Parse(provider.Config).ApiKey;
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    private string? ClaudeStoredAccessToken()
    {
        var node = ReadJson(Path.Combine(HomeDirectory(), ".claude", ".credentials.json"))?["claudeAiOauth"];
        var token = node?["accessToken"]?.GetValue<string>();
        var expiresAt = node?["expiresAt"] is { } expiry && expiry.GetValueKind() == JsonValueKind.Number ? expiry.GetValue<long>() : 0;
        return !string.IsNullOrEmpty(token) && expiresAt > _clock.GetUtcNow().ToUnixTimeMilliseconds() + 60_000 ? token : null;
    }

    private string? CopilotToken()
    {
        var path = Path.Combine(HomeDirectory(), ".copilot", "config.json");
        if (!File.Exists(path)) return null;
        // The file opens with // comment lines that belong to the CLI.
        var text = string.Join('\n', File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException) { return null; }

        var last = root?["lastLoggedInUser"];
        var host = last?["host"]?.GetValue<string>();
        var login = last?["login"]?.GetValue<string>();
        if (host is null || login is null) return null;
        return root?["authTokens"]?[$"{host}:{login}"]?["token"]?.GetValue<string>();
    }

    private string HomeDirectory()
        => _environment.Get("HOME") is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static JsonNode? ReadJson(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) : null; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
}
