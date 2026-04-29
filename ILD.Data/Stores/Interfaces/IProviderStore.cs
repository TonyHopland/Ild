using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Data.Stores.Interfaces;

public interface IProviderStore
{
    Task<AiProvider?> GetAiProviderByIdAsync(Guid id);
    Task<AiProvider?> GetAiProviderByNameAsync(string name);
    Task<AiProvider?> GetDefaultAiProviderAsync();
    Task<AiProvider?> GetFirstAiProviderAsync();
    Task<IReadOnlyList<string>> GetAiProviderNamesAsync();
    Task<IReadOnlyList<AiProvider>> GetAllAiProvidersAsync();
    Task CreateAiProviderAsync(AiProvider provider);
    Task UpdateAiProviderAsync(AiProvider provider);
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
    Task<LoopTemplate?> GetLoopTemplateByIdAsync(Guid id);
    Task<LoopTemplate?> GetLoopTemplateByVersionIdAsync(Guid versionId);
    Task CreateLoopTemplateAsync(LoopTemplate template);
    Task UpdateLoopTemplateAsync(LoopTemplate template);
}
