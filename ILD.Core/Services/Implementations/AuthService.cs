using System.Security.Cryptography;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

public class AuthService : IAuthService
{
    private const int Pbkdf2Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string DefaultUsername = "admin";

    private readonly IAuthStore _authStore;

    public AuthService(IAuthStore authStore)
    {
        _authStore = authStore;
    }

    public async Task<AuthResult> LoginAsync(string username, string password)
    {
        var envPassword = Environment.GetEnvironmentVariable("ILD_PASSWORD");
        var user = await _authStore.GetByUsernameAsync(username);

        if (user == null && username == DefaultUsername && !string.IsNullOrEmpty(envPassword))
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                Username = DefaultUsername,
                PasswordHash = HashPassword(envPassword),
                CreatedAt = DateTime.UtcNow,
            };
            await _authStore.CreateUserAsync(user);
        }

        if (user == null || !VerifyPassword(password, user.PasswordHash))
            return new AuthResult(false, null, null, "Invalid credentials");

        user.SessionToken = GenerateToken();
        user.UpdatedAt = DateTime.UtcNow;
        await _authStore.UpdateUserAsync(user);

        return new AuthResult(true, user.SessionToken, user.Username, null);
    }

    public async Task LogoutAsync(string sessionId)
    {
        var user = await _authStore.GetBySessionTokenAsync(sessionId);
        if (user == null) return;
        user.SessionToken = null;
        user.UpdatedAt = DateTime.UtcNow;
        await _authStore.UpdateUserAsync(user);
    }

    public async Task<bool> ValidateSessionAsync(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        return await _authStore.ExistsBySessionTokenAsync(sessionId);
    }

    public async Task<string?> GetUsernameAsync(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        var user = await _authStore.GetBySessionTokenAsync(sessionId);
        return user?.Username;
    }

    public Task<string> GenerateApiKeyAsync(string description)
        => Task.FromResult(GenerateToken());

    public Task RevokeApiKeyAsync(string apiKey) => Task.CompletedTask;

    public Task<IEnumerable<ApiKeyRecord>> GetApiKeysAsync()
        => Task.FromResult<IEnumerable<ApiKeyRecord>>(Array.Empty<ApiKeyRecord>());

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Pbkdf2Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var iters)) return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iters, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string GenerateToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
}
