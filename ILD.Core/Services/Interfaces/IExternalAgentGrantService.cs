using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

/// <summary>A live grant's identity, as authentication resolves it from its token.</summary>
public sealed record ExternalAgentGrantIdentity(Guid Id, string Name, ExternalAgentGrantScope Scope);

public sealed record IssuedExternalAgentGrant(ExternalAgentGrant Grant, string Token);

public enum ExternalAgentGrantStatus
{
    Active,
    Expired,
    Revoked,
}

/// <summary>
/// The temporary access grants an operator hands to AI sessions outside ILD (ADR-0037). Only a hash of
/// each token is kept, so a token cannot be read back after <see cref="CreateAsync"/> returned it.
/// </summary>
public interface IExternalAgentGrantService
{
    /// <summary>The longest a grant may live. A longer request is refused, not shortened.</summary>
    static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(7);

    /// <summary>The longest a name may be.</summary>
    const int MaxNameLength = 200;

    /// <summary>Null when <paramref name="lifetime"/> is acceptable, else what is wrong with it.</summary>
    static string? ValidateLifetime(TimeSpan lifetime)
        => lifetime <= TimeSpan.Zero ? "A grant must last longer than zero."
            : lifetime > MaxLifetime ? $"A grant may last at most {MaxLifetime.TotalDays:0} days."
            : null;

    Task<IssuedExternalAgentGrant> CreateAsync(
        string name, TimeSpan lifetime, ExternalAgentGrantScope scope, string createdBy, CancellationToken ct = default);

    /// <summary>The grant a token belongs to when it is neither revoked nor expired, else null.</summary>
    Task<ExternalAgentGrantIdentity?> AuthenticateAsync(string token, CancellationToken ct = default);

    /// <summary>Live grants and those that ended in the last week, newest first.</summary>
    Task<IReadOnlyList<ExternalAgentGrant>> ListAsync(CancellationToken ct = default);

    Task<ExternalAgentGrant?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>False when no live grant has this id.</summary>
    Task<bool> RevokeAsync(Guid id, CancellationToken ct = default);

    /// <summary>Revokes every live grant and returns how many there were.</summary>
    Task<int> RevokeAllAsync(CancellationToken ct = default);

    /// <summary>Records one request the grant made and stamps when it was last used, and from where unless <paramref name="from"/> is null.</summary>
    Task RecordActivityAsync(Guid grantId, string method, string path, int statusCode, string? from, CancellationToken ct = default);

    /// <summary>The grant's most recent requests, newest first.</summary>
    Task<IReadOnlyList<ExternalAgentGrantActivity>> ListActivityAsync(Guid grantId, int take, CancellationToken ct = default);

    static ExternalAgentGrantStatus StatusOf(ExternalAgentGrant grant, DateTime now)
        => grant.RevokedAt is not null ? ExternalAgentGrantStatus.Revoked
            : grant.ExpiresAt <= now ? ExternalAgentGrantStatus.Expired
            : ExternalAgentGrantStatus.Active;
}
