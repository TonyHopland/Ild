using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

public interface IAuthService
{
    /// <param name="userAgent">
    /// Recorded on the session purely so the active-sessions list is readable by
    /// a human ("Firefox on the laptop"); nothing authenticates against it.
    /// </param>
    /// <param name="createdFromIp">Likewise — a label, not a check.</param>
    Task<AuthResult> LoginAsync(string username, string password, string? userAgent = null, string? createdFromIp = null);

    /// <summary>Revokes the one session the token belongs to, not the user's others.</summary>
    Task LogoutAsync(string sessionToken);

    Task<bool> ValidateSessionAsync(string sessionToken);
    Task<string?> GetUsernameAsync(string sessionToken);

    /// <summary>
    /// The caller's live sessions, newest activity first, with the caller's own
    /// marked <see cref="UserSessionInfo.IsCurrent"/>. Empty when the token is
    /// not a live session.
    /// </summary>
    Task<IReadOnlyList<UserSessionInfo>> GetSessionsAsync(string sessionToken);

    /// <summary>
    /// Revokes one of the caller's own sessions. False when the token is not
    /// live or the id is not a live session of the same user.
    /// </summary>
    Task<bool> RevokeSessionAsync(string sessionToken, Guid sessionId);

    /// <summary>
    /// Signs out everywhere else: revokes every live session of the caller
    /// except the calling one. Returns how many were revoked.
    /// </summary>
    Task<int> RevokeOtherSessionsAsync(string sessionToken);
}
