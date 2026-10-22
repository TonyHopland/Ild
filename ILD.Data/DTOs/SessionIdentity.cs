namespace ILD.Data.DTOs;

/// <summary>
/// Who a live session belongs to. <see cref="Username"/> is null when the
/// session's user cannot be resolved; the session is still authenticated.
/// </summary>
public record SessionIdentity(string? Username);
