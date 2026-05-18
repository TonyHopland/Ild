using ILD.Data.Entities;

namespace ILD.Core.Services.Interfaces;

public sealed record ResolvedRemoteRepository(
    RemoteProvider Provider,
    string ProviderType,
    string ApiBase,
    string Owner,
    string Repo,
    IRemoteGitProviderAdapter Adapter);