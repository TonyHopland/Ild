namespace ILD.Core.DTOs;

public class LoginResponse
{
    public LoginResponse(string? token, string? username)
    {
        Token = token ?? string.Empty;
        Username = username ?? string.Empty;
        ExpiresAt = DateTime.UtcNow.AddHours(24);
    }

    public string Token { get; }
    public string Username { get; }
    public DateTime ExpiresAt { get; }
}
