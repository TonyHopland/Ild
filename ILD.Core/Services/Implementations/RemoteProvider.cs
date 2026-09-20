using ILD.Data.DTOs;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<RemoteProviderService>? _log;

    public RemoteProviderService(
        IProviderStore providerStore,
        IEnumerable<IRemoteGitProviderAdapter> adapters,
        HttpClient http,
        ILogger<RemoteProviderService>? log = null)
    {
        _providerStore = providerStore;
        _adapters = adapters.ToArray();
        _http = http;
        _log = log;
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

    public async Task<bool> EnablePullRequestAutoMergeAsync(string repoUrl, string prNumber)
    {
        var resolved = await ResolveAsync(repoUrl);
        if (resolved == null) return false;
        try { return await resolved.Adapter.EnablePullRequestAutoMergeAsync(_http, resolved, prNumber); }
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

    public async Task<RemoteCiLog> GetCheckLogAsync(string repoUrl, string checkId, int tailLines, int offset)
    {
        var resolved = await ResolveAsync(repoUrl);
        if (resolved == null)
            return RemoteCiLog.Unavailable("No remote provider is configured for this repository.");
        try { return await resolved.Adapter.GetCheckLogAsync(_http, resolved, checkId, tailLines, offset); }
        catch (Exception ex)
        {
            // The message goes to an agent, so it says only what the agent can
            // act on. A transport failure's detail (hosts, URLs, tokens in a
            // message) belongs in the server log, which is also the only place
            // it would otherwise be lost — every other method here swallows.
            _log?.LogWarning(ex, "CI log fetch failed for check {CheckId} on {RepoUrl}", checkId, repoUrl);
            return RemoteCiLog.Unavailable("Could not read the log for this check — the provider request failed.");
        }
    }

    public async Task<bool> DeleteBranchAsync(string repoUrl, string branchName)
    {
        var resolved = await ResolveAsync(repoUrl);
        if (resolved == null) return false;
        try { return await resolved.Adapter.DeleteBranchAsync(_http, resolved, branchName); }
        catch { return false; }
    }

    public async Task<RemotePrWriteResult> CreatePullRequestCommentAsync(string repoUrl, string prNumber, string body)
    {
        var resolved = await ResolveAsync(repoUrl);
        if (resolved == null) return NoProvider;
        try { return await resolved.Adapter.CreatePullRequestCommentAsync(_http, resolved, prNumber, body); }
        catch (Exception ex) { return Refused(ex, "PR comment post", repoUrl); }
    }

    public async Task<RemotePrReviewLedger> GetPullRequestReviewLedgerAsync(string repoUrl, string prNumber)
    {
        var resolved = await ResolveAsync(repoUrl);
        if (resolved == null)
            return RemotePrReviewLedger.Unavailable("No remote provider is configured for this repository.");
        try { return await resolved.Adapter.GetPullRequestReviewLedgerAsync(_http, resolved, prNumber); }
        catch (Exception ex)
        {
            // As with the CI log: the agent-facing message says only what the
            // agent can act on, and the transport detail goes to the server log,
            // which is otherwise the only place it would be lost.
            _log?.LogWarning(ex, "Review ledger fetch failed for PR {PrNumber} on {RepoUrl}", prNumber, repoUrl);
            return RemotePrReviewLedger.Unavailable("Could not read the review on this pull request — the provider request failed.");
        }
    }

    public async Task<RemotePrWriteResult> ReplyToReviewThreadAsync(string repoUrl, string prNumber, string commentId, string body)
    {
        var resolved = await ResolveAsync(repoUrl);
        if (resolved == null) return NoProvider;
        try { return await resolved.Adapter.ReplyToReviewThreadAsync(_http, resolved, prNumber, commentId, body); }
        catch (Exception ex) { return Refused(ex, "review reply", repoUrl); }
    }

    public async Task<RemotePrWriteResult> ResolveReviewThreadAsync(string repoUrl, string prNumber, string threadId)
    {
        var resolved = await ResolveAsync(repoUrl);
        if (resolved == null) return NoProvider;
        try { return await resolved.Adapter.ResolveReviewThreadAsync(_http, resolved, prNumber, threadId); }
        catch (Exception ex) { return Refused(ex, "thread resolution", repoUrl); }
    }

    public async Task<bool> SupportsThreadResolutionAsync(string repoUrl)
    {
        var resolved = await ResolveAsync(repoUrl);
        return resolved?.Adapter.SupportsThreadResolution ?? false;
    }

    private static readonly RemotePrWriteResult NoProvider =
        new(false, null, "No remote provider is configured for this repository.");

    private RemotePrWriteResult Refused(Exception ex, string what, string repoUrl)
    {
        _log?.LogWarning(ex, "{What} failed on {RepoUrl}", what, repoUrl);
        return new RemotePrWriteResult(false, null, "The provider request failed.");
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
