using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations.ModelGateway;

/// <summary>
/// The first path segment under <c>/gateway</c> names where a request is forwarded, and which of the
/// provider's credentials it carries. See ADR-0031.
/// </summary>
public static class GatewayRoutes
{
    /// <summary>The header a CLI that cannot carry the token in its credential is configured to send it in.</summary>
    public const string TokenHeader = "X-ILD-Gateway-Token";

    public const string Anthropic = "anthropic";
    public const string ChatGpt = "chatgpt";
    public const string GitHub = "github";
    public const string Upstream = "upstream";

    public const string AnthropicHost = "api.anthropic.com";
    public const string GitHubApiHost = "api.github.com";
    public const string ChatGptHost = "chatgpt.com";

    /// <summary>The route a provider type's CLI talks to the gateway on, or null for a type that does not use the gateway.</summary>
    public static string? RouteFor(string providerType) => providerType switch
    {
        "claude-code" => Anthropic,
        "codex" => ChatGpt,
        "copilot" => GitHub,
        "opencode" or "pi" => Upstream,
        _ => null,
    };

    /// <summary>
    /// The hosts the egress proxy refuses the agent while it holds a gateway lease for this provider, so
    /// the gateway is the only way to the model. A pattern is an exact host or a leading-dot suffix.
    /// </summary>
    public static IReadOnlyList<string> ModelHosts(AiProvider provider) => RouteFor(provider.Type) switch
    {
        Anthropic => [AnthropicHost],
        ChatGpt => [ChatGptHost, "api.openai.com", "auth.openai.com"],
        GitHub => [".githubcopilot.com"],
        Upstream when Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri) => [uri.Host],
        _ => [],
    };
}
