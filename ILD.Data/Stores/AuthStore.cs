using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public class AuthStore : IAuthStore
{
    private readonly AppDbContext _db;

    public AuthStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<User?> GetByUsernameAsync(string username)
        => await _db.Users.FirstOrDefaultAsync(u => u.Username == username);

    public async Task CreateUserAsync(User user)
    {
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
    }

    public async Task CreateSessionAsync(UserSession session)
    {
        _db.UserSessions.Add(session);
        await _db.SaveChangesAsync();
    }

    public Task<UserSession?> GetSessionByTokenHashAsync(string tokenHash)
        => _db.UserSessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.TokenHash == tokenHash);

    public async Task<IReadOnlyList<UserSession>> GetUnrevokedSessionsAsync(Guid userId)
        => await _db.UserSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync();

    public async Task TouchSessionAsync(Guid sessionId, DateTime lastSeenAt)
    {
        var session = await _db.UserSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session == null) return;
        session.LastSeenAt = lastSeenAt;
        await _db.SaveChangesAsync();
    }

    public async Task<bool> RevokeSessionAsync(Guid userId, Guid sessionId, DateTime revokedAt)
    {
        var session = await _db.UserSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null);
        if (session == null) return false;
        session.RevokedAt = revokedAt;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<int> RevokeOtherSessionsAsync(Guid userId, Guid keepSessionId, DateTime revokedAt)
    {
        var others = await _db.UserSessions
            .Where(s => s.UserId == userId && s.Id != keepSessionId && s.RevokedAt == null)
            .ToListAsync();
        if (others.Count == 0) return 0;

        foreach (var session in others)
            session.RevokedAt = revokedAt;

        await _db.SaveChangesAsync();
        return others.Count;
    }
}
