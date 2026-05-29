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
        await SaveWithSingleDefaultAsync(provider, () => _db.AiProviders.Add(provider));
    }

    public async Task UpdateAiProviderAsync(AiProvider provider)
    {
        await SaveWithSingleDefaultAsync(provider, () => _db.AiProviders.Update(provider));
    }

    private async Task SaveWithSingleDefaultAsync(AiProvider provider, Action stage)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        if (provider.IsDefault)
        {
            await _db.AiProviders
                .Where(p => p.IsDefault && p.Id != provider.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, false));
        }
        stage();
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
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
