using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Data.Stores;

public class ProviderStore : IProviderStore
{
    private readonly AppDbContext _db;

    public ProviderStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<AiProvider?> GetAiProviderByIdAsync(Guid id)
        => await _db.AiProviders.Include(p => p.Tags).FirstOrDefaultAsync(p => p.Id == id);

    public async Task<AiProvider?> GetAiProviderByNameAsync(string name)
        => await _db.AiProviders.FirstOrDefaultAsync(p => p.Name == name);

    public async Task<AiProvider?> GetDefaultAiProviderAsync()
        => await _db.AiProviders.FirstOrDefaultAsync(p => p.IsDefault);

    public async Task<AiProvider?> GetAiProviderByTagAsync(string tag)
    {
        var normalized = AiProviderTag.Normalize(tag);
        return await _db.AiProviders.FirstOrDefaultAsync(p => p.Tags.Any(t => t.NormalizedName == normalized));
    }

    public async Task<AiProvider?> GetFirstAiProviderAsync()
        => await _db.AiProviders.FirstOrDefaultAsync();

    public async Task<IReadOnlyList<string>> GetAiProviderNamesAsync()
        => await _db.AiProviders.Select(p => p.Name).ToListAsync();

    public async Task<IReadOnlyList<AiProvider>> GetAllAiProvidersAsync()
        => await _db.AiProviders.Include(p => p.Tags).ToListAsync();

    public async Task CreateAiProviderAsync(AiProvider provider, IReadOnlyList<string>? tags = null)
    {
        await SaveAsync(provider, () => _db.AiProviders.Add(provider), tags);
    }

    public async Task UpdateAiProviderAsync(AiProvider provider, IReadOnlyList<string>? tags = null)
    {
        // Only the provider row: marking its loaded tag rows modified too would
        // fail the save when a concurrent save has moved one of them away.
        await SaveAsync(provider, () => _db.Entry(provider).State = EntityState.Modified, tags);
    }

    /// <summary>
    /// Saves the provider in one transaction with the moves that keep a single
    /// default and a single holder per tag, so a failed save leaves the other
    /// providers' default flag and tags where they were.
    /// </summary>
    private async Task SaveAsync(AiProvider provider, Action stage, IReadOnlyList<string>? tags)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        if (provider.IsDefault)
        {
            await _db.AiProviders
                .Where(p => p.IsDefault && p.Id != provider.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, false));
        }
        stage();
        if (tags is not null)
            await StageTagsAsync(provider, tags);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private async Task StageTagsAsync(AiProvider provider, IReadOnlyList<string> tags)
    {
        var wanted = tags.ToDictionary(AiProviderTag.Normalize, t => t, StringComparer.Ordinal);
        var wantedNames = wanted.Keys.ToList();
        await _db.AiProviderTags
            .Where(t => t.AiProviderId != provider.Id && wantedNames.Contains(t.NormalizedName))
            .ExecuteDeleteAsync();

        var own = await _db.AiProviderTags.Where(t => t.AiProviderId == provider.Id).ToListAsync();
        foreach (var tag in own)
        {
            if (wanted.Remove(tag.NormalizedName, out var spelling))
            {
                tag.Name = spelling;
            }
            else
            {
                provider.Tags.Remove(tag);
                _db.AiProviderTags.Remove(tag);
            }
        }
        foreach (var (normalized, name) in wanted)
        {
            _db.AiProviderTags.Add(new AiProviderTag
            {
                Id = Guid.NewGuid(),
                AiProviderId = provider.Id,
                Name = name,
                NormalizedName = normalized,
                CreatedAt = DateTime.UtcNow,
            });
        }
    }

    public async Task DeleteAiProviderAsync(AiProvider provider)
    {
        _db.AiProviders.Remove(provider);
        await _db.SaveChangesAsync();
    }

    public async Task<RemoteProvider?> GetRemoteProviderByIdAsync(Guid id)
        => await _db.RemoteProviders.FindAsync(id).AsTask();

    public async Task<IReadOnlyList<RemoteProvider>> GetAllRemoteProvidersAsync()
        => await _db.RemoteProviders.ToListAsync();

    public async Task CreateRemoteProviderAsync(RemoteProvider provider)
    {
        _db.RemoteProviders.Add(provider);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateRemoteProviderAsync(RemoteProvider provider)
    {
        _db.RemoteProviders.Update(provider);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteRemoteProviderAsync(RemoteProvider provider)
    {
        _db.RemoteProviders.Remove(provider);
        await _db.SaveChangesAsync();
    }

    public async Task<Repository?> GetRepositoryByIdAsync(Guid id)
        => await _db.Repositories.FindAsync(id).AsTask();

    public async Task<IReadOnlyList<Repository>> GetAllRepositoriesAsync()
        => await _db.Repositories.ToListAsync();

    public async Task CreateRepositoryAsync(Repository repository)
    {
        _db.Repositories.Add(repository);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateRepositoryAsync(Repository repository)
    {
        _db.Repositories.Update(repository);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteRepositoryAsync(Repository repository)
    {
        _db.Repositories.Remove(repository);
        await _db.SaveChangesAsync();
    }
}
