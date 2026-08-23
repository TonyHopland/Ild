using System.Security.Cryptography;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Security;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Password check plus the lifecycle of a <see cref="UserSession"/>.
///
/// Sessions are plural and independent: a sign-in inserts a row, a sign-out
/// revokes that row, and neither touches the user's other devices. The bearer
/// token is minted here and immediately forgotten — only <see cref="SessionTokenHasher"/>
/// output is stored, and every lookup hashes the presented token to find its row.
///
/// A session dies three ways: revoked (<see cref="UserSession.RevokedAt"/>),
/// idle past <see cref="AppSettingKeys.SessionIdleDays"/>, or older than the
/// <see cref="AppSettingKeys.SessionMaxDays"/> cap stamped into
/// <see cref="UserSession.ExpiresAt"/> when it was created.
/// </summary>
public class AuthService : IAuthService
{
    private const int Pbkdf2Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string DefaultUsername = "admin";

    /// <summary>
    /// How stale <see cref="UserSession.LastSeenAt"/> has to be before a request
    /// writes it back. Every authenticated request would otherwise be a write.
    /// </summary>
    private static readonly TimeSpan LastSeenWriteInterval = TimeSpan.FromMinutes(1);

    private readonly IAuthStore _authStore;
    private readonly IAppSettingStore _settings;

    /// <summary>
    /// Memoized for the lifetime of this (scoped, per-request) instance: the
    /// authentication handler validates the session and then resolves its
    /// username, and neither call should re-read the same setting row.
    /// </summary>
    private int? _idleDays;

    private readonly string? _configuredPassword;
    private readonly string _bootstrapUsername;

    public AuthService(IAuthStore authStore, IAppSettingStore settings)
    {
        _authStore = authStore;
        _settings = settings;
        // Credentials stay env vars — they are secrets. Expiry lives in
        // AppSettings because it is a preference an operator changes from the
        // Settings page without restarting.
        _configuredPassword = Environment.GetEnvironmentVariable("ILD_PASSWORD");
        var configuredUsername = Environment.GetEnvironmentVariable("ILD_USERNAME");
        _bootstrapUsername = string.IsNullOrWhiteSpace(configuredUsername)
            ? DefaultUsername
            : configuredUsername.Trim();
    }

    public async Task<AuthResult> LoginAsync(
        string username,
        string password,
        string? userAgent = null,
        string? createdFromIp = null)
    {
        var user = await _authStore.GetByUsernameAsync(username);

        if (user == null && username == _bootstrapUsername && !string.IsNullOrEmpty(_configuredPassword))
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                Username = _bootstrapUsername,
                PasswordHash = HashPassword(_configuredPassword),
                CreatedAt = DateTime.UtcNow,
            };
            await _authStore.CreateUserAsync(user);
        }

        if (user == null || !VerifyPassword(password, user.PasswordHash))
            return new AuthResult(false, null, null, "Invalid credentials");

        var now = DateTime.UtcNow;
        var maxDays = await ReadDaysAsync(AppSettingKeys.SessionMaxDays, AppSettingKeys.DefaultSessionMaxDays);
        var token = GenerateToken();

        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = SessionTokenHasher.Hash(token),
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = maxDays > 0 ? now.AddDays(maxDays) : null,
            UserAgent = Truncate(userAgent, 512),
            CreatedFromIp = Truncate(createdFromIp, 64),
        };
        await _authStore.CreateSessionAsync(session);

        return new AuthResult(true, token, user.Username, null, session.ExpiresAt);
    }

    public async Task LogoutAsync(string sessionToken)
    {
        var session = await GetLiveSessionAsync(sessionToken);
        if (session == null) return;
        await _authStore.RevokeSessionAsync(session.UserId, session.Id, DateTime.UtcNow);
    }

    public async Task<bool> ValidateSessionAsync(string sessionToken)
    {
        var session = await GetLiveSessionAsync(sessionToken);
        if (session == null) return false;

        var now = DateTime.UtcNow;
        if (now - session.LastSeenAt >= LastSeenWriteInterval)
            await _authStore.TouchSessionAsync(session.Id, now);

        return true;
    }

    public async Task<string?> GetUsernameAsync(string sessionToken)
        => (await GetLiveSessionAsync(sessionToken))?.User?.Username;

    public async Task<IReadOnlyList<UserSessionInfo>> GetSessionsAsync(string sessionToken)
    {
        var current = await GetLiveSessionAsync(sessionToken);
        if (current == null) return Array.Empty<UserSessionInfo>();

        var now = DateTime.UtcNow;
        var idleDays = await IdleDaysAsync();

        // Un-revoked but timed-out sessions are already dead; showing them would
        // invite the operator to revoke something that cannot be used anyway.
        return (await _authStore.GetUnrevokedSessionsAsync(current.UserId))
            .Where(s => IsLive(s, now, idleDays))
            .Select(s => new UserSessionInfo(
                s.Id,
                s.CreatedAt,
                s.LastSeenAt,
                s.ExpiresAt,
                s.UserAgent,
                s.CreatedFromIp,
                s.Id == current.Id))
            .ToList();
    }

    public async Task<bool> RevokeSessionAsync(string sessionToken, Guid sessionId)
    {
        var current = await GetLiveSessionAsync(sessionToken);
        if (current == null) return false;
        return await _authStore.RevokeSessionAsync(current.UserId, sessionId, DateTime.UtcNow);
    }

    public async Task<int> RevokeOtherSessionsAsync(string sessionToken)
    {
        var current = await GetLiveSessionAsync(sessionToken);
        if (current == null) return 0;
        return await _authStore.RevokeOtherSessionsAsync(current.UserId, current.Id, DateTime.UtcNow);
    }

    /// <summary>
    /// The session a token names, or null when there is none, it was revoked, or
    /// it has timed out. The single gate every public method above goes through.
    /// </summary>
    private async Task<UserSession?> GetLiveSessionAsync(string sessionToken)
    {
        if (string.IsNullOrEmpty(sessionToken)) return null;

        var session = await _authStore.GetSessionByTokenHashAsync(SessionTokenHasher.Hash(sessionToken));
        if (session == null) return null;

        return IsLive(session, DateTime.UtcNow, await IdleDaysAsync()) ? session : null;
    }

    private static bool IsLive(UserSession session, DateTime now, int idleDays)
    {
        if (session.RevokedAt != null) return false;
        if (session.ExpiresAt != null && session.ExpiresAt <= now) return false;
        if (idleDays > 0 && session.LastSeenAt.AddDays(idleDays) <= now) return false;
        return true;
    }

    private async Task<int> IdleDaysAsync()
        => _idleDays ??= await ReadDaysAsync(AppSettingKeys.SessionIdleDays, AppSettingKeys.DefaultSessionIdleDays);

    private async Task<int> ReadDaysAsync(string key, int fallback)
    {
        var setting = await _settings.GetByKeyAsync(key);
        return setting != null && int.TryParse(setting.Value, out var days) && days >= 0 ? days : fallback;
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? null
            : value.Length <= max ? value
            : value[..max];

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
