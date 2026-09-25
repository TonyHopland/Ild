using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

public interface IProviderStore
{
    Task<AiProvider?> GetAiProviderByIdAsync(Guid id);
    Task<AiProvider?> GetAiProviderByNameAsync(string name);
    Task<AiProvider?> GetDefaultAiProviderAsync();
    Task<AiProvider?> GetAiProviderByTagAsync(string tag);
    Task<AiProvider?> GetFirstAiProviderAsync();
    Task<IReadOnlyList<string>> GetAiProviderNamesAsync();
    Task<IReadOnlyList<AiProvider>> GetAllAiProvidersAsync();
    /// <param name="tags">
    /// The provider's tags, trimmed and without case-duplicates; a tag another
    /// provider holds moves to this one. Null leaves the tags as they are.
    /// </param>
    Task CreateAiProviderAsync(AiProvider provider, IReadOnlyList<string>? tags = null);
    /// <inheritdoc cref="CreateAiProviderAsync"/>
    Task UpdateAiProviderAsync(AiProvider provider, IReadOnlyList<string>? tags = null);
    Task DeleteAiProviderAsync(AiProvider provider);
    Task<RemoteProvider?> GetRemoteProviderByIdAsync(Guid id);
    Task<IReadOnlyList<RemoteProvider>> GetAllRemoteProvidersAsync();
    Task CreateRemoteProviderAsync(RemoteProvider provider);
    Task UpdateRemoteProviderAsync(RemoteProvider provider);
    Task DeleteRemoteProviderAsync(RemoteProvider provider);
    Task<Repository?> GetRepositoryByIdAsync(Guid id);
    Task<IReadOnlyList<Repository>> GetAllRepositoriesAsync();
    Task CreateRepositoryAsync(Repository repository);
    Task UpdateRepositoryAsync(Repository repository);
    Task DeleteRepositoryAsync(Repository repository);
}
