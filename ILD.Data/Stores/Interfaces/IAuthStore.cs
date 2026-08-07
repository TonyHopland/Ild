using ILD.Data.Entities;

namespace ILD.Data.Stores.Interfaces;

/// <summary>
/// Users and their live sign-ins. Sessions are addressed by the hash of their
/// bearer token — the plaintext token never reaches this layer, so nothing
/// stored here can be replayed as a login.
/// </summary>
public interface IAuthStore
{
    Task<User?> GetByUsernameAsync(string username);
    Task CreateUserAsync(User user);

    Task CreateSessionAsync(UserSession session);

    /// <summary>
    /// The session for a token hash with its <see cref="UserSession.User"/>
    /// loaded, revoked and expired ones included — validity is the caller's
    /// policy call, not the store's.
    /// </summary>
    Task<UserSession?> GetSessionByTokenHashAsync(string tokenHash);

    /// <summary>Every un-revoked session for a user, most recently seen first.</summary>
    Task<IReadOnlyList<UserSession>> GetUnrevokedSessionsAsync(Guid userId);

    Task TouchSessionAsync(Guid sessionId, DateTime lastSeenAt);

    /// <summary>
    /// Revokes one session, scoped to its owner so a token can never revoke a
    /// session it does not own. False when there was no such live session.
    /// </summary>
    Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, DateTime revokedAt);

    /// <summary>
    /// Revokes every live session of a user except <paramref name="keepSessionId"/>
    /// — "sign out everywhere else". Returns how many were revoked.
    /// </summary>
    Task<int> RevokeOtherSessionsAsync(Guid userId, Guid keepSessionId, DateTime revokedAt);
}
