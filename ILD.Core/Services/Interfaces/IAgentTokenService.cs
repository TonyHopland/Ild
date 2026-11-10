using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

/// <summary>A live agent token's owner: the run or chat the token was issued for.</summary>
public sealed record AgentTokenIdentity(Guid TokenId, AgentTokenSource Source, Guid SourceId);

/// <summary>
/// The agent API tokens ILD issues, one per agent launch. Only a hash is kept, so a token cannot be read back.
/// </summary>
public interface IAgentTokenService
{
    /// <summary>A new token for <paramref name="source"/> and its id. The plaintext is returned once and never stored.</summary>
    Task<IssuedAgentToken> IssueAsync(AgentTokenSource source, Guid sourceId, DateTime? expiresAt = null, CancellationToken ct = default);

    /// <summary>The owner of a token that is neither revoked nor expired, else null.</summary>
    Task<AgentTokenIdentity?> AuthenticateAsync(string token, CancellationToken ct = default);

    Task RevokeAsync(Guid tokenId, CancellationToken ct = default);

    /// <summary>Revokes every live token of one run or chat.</summary>
    Task RevokeAllForAsync(AgentTokenSource source, Guid sourceId, CancellationToken ct = default);

    /// <summary>Revokes every live token: none can belong to a launch of a process that is no longer running.</summary>
    Task RevokeAllLiveAsync(CancellationToken ct = default);
}

public sealed record IssuedAgentToken(Guid TokenId, string Token);
