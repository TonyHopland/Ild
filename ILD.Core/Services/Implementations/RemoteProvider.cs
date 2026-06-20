using ILD.Data.DTOs;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Forgejo / Gitea REST API client. Repo URL is expected to be
/// "https://host/owner/repo" or "https://host/owner/repo.git".
/// </summary>
public class RemoteProviderService : IRemoteProvider
{
    private readonly IProviderStore _providerStore;
    private readonly IReadOnlyList<IRemoteGitProviderAdapter> _adapters;
    private readonly HttpClient _http;

    public RemoteProviderService(IProviderStore providerStore, IEnumerable<IRemoteGitProviderAdapter> adapters, HttpClient http)
    {
        _providerStore = providerStore;
        _adapters = adapters.ToArray();
        _http = http;
    }

    public async Task<RemotePrResult> CreatePullRequestAsync(string repoUrl, string sourceBranch, string targetBranch, string title, string body)
    {
        var resolved = await ResolveAsync(repoUrl);
        if (resolved == null) return new RemotePrResult(null, null, RemotePrStatus.Open, "no provider configured");
        try
        {
            return await resolved.Adapter.CreatePullRequestAsync(_http, resolved, sourceBranch, targetBranch, title, body);
        }
        catch (Exception ex)
        {
            return new RemotePrResult(null, null, RemotePrStatus.Open, ex.Message);
        }
    }

    public async Task<bool> MergePullRequestAsync(string repoUrl, string prNumber)
    {
        var resolved = await ResolveAsync(repoUrl);
        if (resolved == null) return false;
        try { return await resolved.Adapter.MergePullRequestAsync(_http, resolved, prNumber); }
        catch { return false; }
    }

    public async Task<IEnumerable<RemotePrComment>> GetPullRequestCommentsAsync(string repoUrl, string prNumber)
    {
        var resolved = await ResolveAsync(repoUrl);
        if (resolved == null) return Array.Empty<RemotePrComment>();
        try { return await resolved.Adapter.GetPullRequestCommentsAsync(_http, resolved, prNumber); }
        catch { return Array.Empty<RemotePrComment>(); }
    }

    public async Task RegisterWebhookAsync(string repoUrl, string callbackUrl)
    {
        var resolved = await ResolveAsync(repoUrl);
        if (resolved == null) return;
        try { await resolved.Adapter.RegisterWebhookAsync(_http, resolved, callbackUrl); }
        catch { }
    }

    public async Task UnregisterWebhookAsync(string repoUrl, string callbackUrl)
    {
        var resolved = await ResolveAsync(repoUrl);
        if (resolved == null) return;

        try { await resolved.Adapter.UnregisterWebhookAsync(_http, resolved, callbackUrl); }
        catch { }
    }

    public async Task<RemotePrStatus> GetPullRequestStatusAsync(string repoUrl, string prNumber)
    {
        var resolved = await ResolveAsync(repoUrl);
        if (resolved == null) return RemotePrStatus.Open;
        try { return await resolved.Adapter.GetPullRequestStatusAsync(_http, resolved, prNumber); }
        catch { return RemotePrStatus.Open; }
    }

    public async Task<RemotePrSnapshot?> GetPullRequestSnapshotAsync(string repoUrl, string prNumber)
    {
        var resolved = await ResolveAsync(repoUrl);
        if (resolved == null) return null;
        try { return await resolved.Adapter.GetPullRequestSnapshotAsync(_http, resolved, prNumber); }
        catch { return null; }
    }

    public async Task<bool> DeleteBranchAsync(string repoUrl, string branchName)
    {
        var resolved = await ResolveAsync(repoUrl);
        if (resolved == null) return false;
        try { return await resolved.Adapter.DeleteBranchAsync(_http, resolved, branchName); }
        catch { return false; }
    }

    public async Task<bool> CreatePullRequestCommentAsync(string repoUrl, string prNumber, string body)
    {
        var resolved = await ResolveAsync(repoUrl);
        if (resolved == null) return false;
        try { return await resolved.Adapter.CreatePullRequestCommentAsync(_http, resolved, prNumber, body); }
        catch { return false; }
    }

    private async Task<ResolvedRemoteRepository?> ResolveAsync(string repoUrl)
    {
        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var repoUri))
            return null;

        var providers = await _providerStore.GetAllRemoteProvidersAsync();
        foreach (var provider in providers)
        {
            var adapter = _adapters.FirstOrDefault(candidate =>
                candidate.ProviderType.Equals(provider.Type, StringComparison.OrdinalIgnoreCase));
            var resolved = adapter?.TryResolve(provider, repoUri);
            if (resolved != null) return resolved;
        }

        return null;
    }
}
