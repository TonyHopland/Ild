using System.Security.Cryptography;

namespace ILD.Api.Configuration;

/// <summary>
/// Provides a process-lifetime auth token used by ILD's own spawned MCP server
/// (and any other in-host agent) to call the agent-scoped API surface without
/// needing to log in as a real user.
///
/// Read in this order:
///   1. <c>ILD_AGENT_TOKEN</c> env var (operator-set; useful in production
///      where you want a stable, externally-known value),
///   2. otherwise, a freshly-generated random token at startup.
///
/// The token is also written back into the process environment so child
/// processes spawned by the API (notably the OpenCode-driven MCP server)
/// inherit it via <c>ILD_API_TOKEN</c> automatically.
/// </summary>
public sealed class AgentAuthTokenProvider
{
    public string Token { get; }

    public AgentAuthTokenProvider()
    {
        var existing = Environment.GetEnvironmentVariable("ILD_AGENT_TOKEN");
        Token = !string.IsNullOrWhiteSpace(existing)
            ? existing
            : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace("+", "-").Replace("/", "_").TrimEnd('=');

        // Make the token visible to spawned children. The OpenCode adapter
        // forwards it as ILD_API_TOKEN into the MCP server child process.
        Environment.SetEnvironmentVariable("ILD_AGENT_TOKEN", Token);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ILD_API_TOKEN")))
            Environment.SetEnvironmentVariable("ILD_API_TOKEN", Token);
    }

    public bool Matches(string? candidate)
        => !string.IsNullOrEmpty(candidate)
           && CryptographicOperations.FixedTimeEquals(
               System.Text.Encoding.UTF8.GetBytes(candidate),
               System.Text.Encoding.UTF8.GetBytes(Token));
}
