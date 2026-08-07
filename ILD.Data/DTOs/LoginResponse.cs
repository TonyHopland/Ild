namespace ILD.Data.DTOs;

public class LoginResponse
{
    public LoginResponse(string? token, string? username, DateTime? expiresAt)
    {
        Token = token ?? string.Empty;
        Username = username ?? string.Empty;
        ExpiresAt = expiresAt;
    }

    public string Token { get; }
    public string Username { get; }

    /// <summary>
    /// When this session stops working no matter how active it is. Null when the
    /// absolute-expiry setting is disabled — idle expiry still applies.
    /// </summary>
    public DateTime? ExpiresAt { get; }
}
