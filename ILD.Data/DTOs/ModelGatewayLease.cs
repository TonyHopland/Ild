namespace ILD.Data.DTOs;

/// <summary>
/// One agent execution's right to use the model gateway (ADR-0031). <see cref="Token"/> is both the
/// run token the gateway authenticates and the placeholder the CLI is given in place of its real
/// credential; disposing the lease ends it, after which the gateway refuses the token.
/// </summary>
public sealed class ModelGatewayLease : IDisposable
{
    private Action? _release;

    public ModelGatewayLease(string token, string baseUrl, Action release)
    {
        Token = token;
        BaseUrl = baseUrl.TrimEnd('/');
        _release = release;
    }

    public string Token { get; }

    /// <summary>The gateway's root, e.g. <c>http://127.0.0.1:8080/gateway</c>.</summary>
    public string BaseUrl { get; }

    public string UrlFor(string route) => $"{BaseUrl}/{route}";

    /// <summary>The token in the shape a GitHub OAuth token has, which the Copilot CLI checks before it makes a request.</summary>
    public string GitHubPlaceholder => "gho_" + Token;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
