using System.Security.Cryptography;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Security;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations;

public sealed class AgentTokenService(AppDbContext db) : IAgentTokenService
{
    public async Task<IssuedAgentToken> IssueAsync(
        AgentTokenSource source, Guid sourceId, DateTime? expiresAt = null, CancellationToken ct = default)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var row = new AgentToken
        {
            Id = Guid.NewGuid(),
            TokenHash = SessionTokenHasher.Hash(token),
            Source = source,
            SourceId = sourceId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
        };
        db.AgentTokens.Add(row);
        await db.SaveChangesAsync(ct);
        return new IssuedAgentToken(row.Id, token);
    }

    public async Task<AgentTokenIdentity?> AuthenticateAsync(string token, CancellationToken ct = default)
    {
        var hash = SessionTokenHasher.Hash(token);
        var now = DateTime.UtcNow;
        var row = await db.AgentTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .Select(t => new { t.Id, t.Source, t.SourceId, t.ExpiresAt })
            .FirstOrDefaultAsync(ct);
        return row is null || row.ExpiresAt <= now
            ? null
            : new AgentTokenIdentity(row.Id, row.Source, row.SourceId);
    }

    public async Task RevokeAsync(Guid tokenId, CancellationToken ct = default)
        => await db.AgentTokens
            .Where(t => t.Id == tokenId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);

    public async Task RevokeAllForAsync(AgentTokenSource source, Guid sourceId, CancellationToken ct = default)
        => await db.AgentTokens
            .Where(t => t.Source == source && t.SourceId == sourceId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);

    public async Task RevokeAllLiveAsync(CancellationToken ct = default)
        => await db.AgentTokens
            .Where(t => t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);
}
