namespace ILD.Data.DTOs;

/// <summary>
/// One row of the active-sessions list. Deliberately carries no token and no
/// token hash: <see cref="Id"/> is the session's own key, which is all the
/// revoke endpoints need and is useless as a credential.
/// </summary>
public record UserSessionInfo(
    Guid Id,
    DateTime CreatedAt,
    DateTime LastSeenAt,
    DateTime? ExpiresAt,
    string? UserAgent,
    string? CreatedFromIp,
    bool IsCurrent
);
