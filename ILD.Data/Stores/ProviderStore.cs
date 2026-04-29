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
        => await _db.AiProviders.FindAsync(id).AsTask();

    public async Task<AiProvider?> GetAiProviderByNameAsync(string name)
        => await _db.AiProviders.FirstOrDefaultAsync(p => p.Name == name);

    public async Task<AiProvider?> GetDefaultAiProviderAsync()
        => await _db.AiProviders.FirstOrDefaultAsync(p => p.IsDefault);

    public async Task<AiProvider?> GetFirstAiProviderAsync()
        => await _db.AiProviders.FirstOrDefaultAsync();

    public async Task<IReadOnlyList<string>> GetAiProviderNamesAsync()
        => await _db.AiProviders.Select(p => p.Name).ToListAsync();

    public async Task<IReadOnlyList<AiProvider>> GetAllAiProvidersAsync()
        => await _db.AiProviders.ToListAsync();

    public async Task CreateAiProviderAsync(AiProvider provider)
    {
        _db.AiProviders.Add(provider);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAiProviderAsync(AiProvider provider)
    {
        await _db.SaveChangesAsync();
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
        await _db.SaveChangesAsync();
    }

    public async Task DeleteRepositoryAsync(Repository repository)
    {
        _db.Repositories.Remove(repository);
        await _db.SaveChangesAsync();
    }

    public async Task<LoopTemplate?> GetLoopTemplateByIdAsync(Guid id)
        => await _db.LoopTemplates.FindAsync(id).AsTask();

    public async Task<LoopTemplate?> GetLoopTemplateByVersionIdAsync(Guid versionId)
        => await _db.LoopTemplates
            .FirstOrDefaultAsync(t => t.Versions.Any(v => v.Id == versionId));

    public async Task CreateLoopTemplateAsync(LoopTemplate template)
    {
        _db.LoopTemplates.Add(template);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateLoopTemplateAsync(LoopTemplate template)
    {
        await _db.SaveChangesAsync();
    }
}
