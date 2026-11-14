using System.Security.Cryptography;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Security;
using Microsoft.EntityFrameworkCore;

namespace ILD.Core.Services.Implementations;

public sealed class ExternalAgentGrantService(AppDbContext db) : IExternalAgentGrantService
{
    public const string TokenPrefix = "ildg_";

    private static readonly TimeSpan EndedGrantRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan ActivityRetention = TimeSpan.FromDays(30);

    public async Task<IssuedExternalAgentGrant> CreateAsync(
        string name, TimeSpan lifetime, ExternalAgentGrantScope scope, string createdBy, CancellationToken ct = default)
    {
        if (IExternalAgentGrantService.ValidateLifetime(lifetime) is { } problem)
            throw new ArgumentOutOfRangeException(nameof(lifetime), problem);
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("A grant needs a name.", nameof(name));
        if (trimmed.Length > IExternalAgentGrantService.MaxNameLength)
            throw new ArgumentException($"A name may be at most {IExternalAgentGrantService.MaxNameLength} characters.", nameof(name));

        var token = TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var now = DateTime.UtcNow;
        var grant = new ExternalAgentGrant
        {
            Id = Guid.NewGuid(),
            Name = trimmed,
            TokenHash = SessionTokenHasher.Hash(token),
            CreatedBy = createdBy,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
            Scope = scope,
        };
        db.ExternalAgentGrants.Add(grant);
        await db.SaveChangesAsync(ct);

        var activityCutoff = now - ActivityRetention;
        await db.ExternalAgentGrantActivities.Where(a => a.At < activityCutoff).ExecuteDeleteAsync(ct);
        return new IssuedExternalAgentGrant(grant, token);
    }

    public async Task<ExternalAgentGrantIdentity?> AuthenticateAsync(string token, CancellationToken ct = default)
    {
        var hash = SessionTokenHasher.Hash(token);
        var now = DateTime.UtcNow;
        return await db.ExternalAgentGrants.AsNoTracking()
            .Where(g => g.TokenHash == hash && g.RevokedAt == null && g.ExpiresAt > now)
            .Select(g => new ExternalAgentGrantIdentity(g.Id, g.Name, g.Scope))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ExternalAgentGrant>> ListAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - EndedGrantRetention;
        return await db.ExternalAgentGrants.AsNoTracking()
            .Where(g => g.ExpiresAt > cutoff && (g.RevokedAt == null || g.RevokedAt > cutoff))
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(ct);
    }

    public Task<ExternalAgentGrant?> GetAsync(Guid id, CancellationToken ct = default)
        => db.ExternalAgentGrants.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);

    public async Task<bool> RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await db.ExternalAgentGrants
            .Where(g => g.Id == id && g.RevokedAt == null && g.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RevokedAt, now), ct) > 0;
    }

    public async Task<int> RevokeAllAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await db.ExternalAgentGrants
            .Where(g => g.RevokedAt == null && g.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RevokedAt, now), ct);
    }

    public async Task RecordActivityAsync(
        Guid grantId, string method, string path, int statusCode, string? from, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        db.ExternalAgentGrantActivities.Add(new ExternalAgentGrantActivity
        {
            Id = Guid.NewGuid(),
            GrantId = grantId,
            At = now,
            Method = Truncate(method, 16),
            Path = Truncate(path, 512),
            StatusCode = statusCode,
            From = from is null ? null : Truncate(from, 64),
        });
        await db.SaveChangesAsync(ct);

        var grants = db.ExternalAgentGrants.Where(g => g.Id == grantId);
        if (from is null)
            await grants.ExecuteUpdateAsync(s => s.SetProperty(g => g.LastUsedAt, now), ct);
        else
            await grants.ExecuteUpdateAsync(s => s.SetProperty(g => g.LastUsedAt, now).SetProperty(g => g.LastUsedFrom, Truncate(from, 64)), ct);

    }

    public async Task<IReadOnlyList<ExternalAgentGrantActivity>> ListActivityAsync(Guid grantId, int take, CancellationToken ct = default)
        => await db.ExternalAgentGrantActivities.AsNoTracking()
            .Where(a => a.GrantId == grantId)
            .OrderByDescending(a => a.At)
            .Take(take)
            .ToListAsync(ct);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
