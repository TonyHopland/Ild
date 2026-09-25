using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

public sealed class ConnectionTester : IConnectionTester
{
    private readonly IReadOnlyList<IRemoteGitProviderAdapter> _adapters;
    private readonly IRepositoryManager _repositories;
    private readonly HttpClient _http;
    private readonly ILogger<ConnectionTester>? _log;

    public ConnectionTester(
        IEnumerable<IRemoteGitProviderAdapter> adapters,
        IRepositoryManager repositories,
        HttpClient http,
        ILogger<ConnectionTester>? log = null)
    {
        _adapters = adapters.ToArray();
        _repositories = repositories;
        _http = http;
        _log = log;
    }

    internal TimeSpan ProviderTimeout { get; set; } = TimeSpan.FromSeconds(20);

    internal TimeSpan RepositoryTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public async Task<ConnectionTestResult> TestRemoteProviderAsync(RemoteProvider provider, CancellationToken ct)
    {
        var adapter = _adapters.FirstOrDefault(candidate =>
            candidate.ProviderType.Equals(provider.Type, StringComparison.OrdinalIgnoreCase));
        var result = adapter is null
            ? new ConnectionTestResult(
                ConnectionTestOutcome.Misconfigured,
                $"ILD does not support the provider type '{provider.Type}'.",
                null)
            : await WithTimeoutAsync(ProviderTimeout, ct, token => adapter.TestConnectionAsync(_http, provider, token));
        // The adapter set this client's Authorization for the request, so its
        // parameter is the credential exactly as the forge received it.
        return Report(result, "Remote provider", provider.Id, provider.ApiKey, _http.DefaultRequestHeaders.Authorization?.Parameter);
    }

    public async Task<ConnectionTestResult> TestRepositoryAsync(Repository repo, RemoteProvider? provider, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repo.CloneUrl))
            return Report(
                new ConnectionTestResult(ConnectionTestOutcome.Misconfigured, "The repository has no clone URL.", null),
                "Repository", repo.Id, provider?.ApiKey);

        var auth = provider is null ? null : new GitAuthOptions(repo.CloneUrl, provider.ApiKey, provider.Type);
        var branch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? null : repo.DefaultBranch.Trim();
        var result = await WithTimeoutAsync(RepositoryTimeout, ct, async token =>
            GitRemoteDiagnosis.Classify(
                await _repositories.ProbeRemoteAsync(repo.CloneUrl, branch, token, auth),
                branch,
                hadApiKey: !string.IsNullOrWhiteSpace(provider?.ApiKey),
                hasProvider: provider is not null));
        return Report(result, "Repository", repo.Id, provider?.ApiKey, RepositoryManager.GitBasicCredential(auth));
    }

    /// <summary>
    /// Bounds a test so a server that never answers reads as unreachable. The
    /// caller giving up is not a timeout, so its cancellation propagates.
    /// </summary>
    private static async Task<ConnectionTestResult> WithTimeoutAsync(
        TimeSpan timeout, CancellationToken ct, Func<CancellationToken, Task<ConnectionTestResult>> test)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        try
        {
            return await test(bounded.Token);
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new ConnectionTestResult(
                ConnectionTestOutcome.Unreachable,
                "The server did not answer in time.",
                $"Timed out after {timeout.TotalSeconds:0.###} s.");
        }
    }

    /// <summary>
    /// The one place a result is finished. A forge or git server may echo the
    /// credential back, so every form it went on the wire as (<paramref name="credentials"/>)
    /// is masked in the evidence first; only then is it trimmed and capped, since
    /// a cut through the credential would leave a fragment no mask matches.
    /// </summary>
    private ConnectionTestResult Report(ConnectionTestResult result, string kind, Guid id, params string?[] credentials)
    {
        _log?.LogInformation("{Kind} {Id} connection test: {Outcome}", kind, id, result.Outcome);
        var masks = credentials
            .Where(credential => !string.IsNullOrEmpty(credential))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(credential => credential!.Length)
            .ToArray();
        return result with
        {
            Message = Mask(result.Message, masks)!,
            Detail = Cap(Mask(result.Detail, masks)),
        };
    }

    private static string? Mask(string? text, string?[] masks)
        => masks.Aggregate(text, (masked, credential) => masked?.Replace(credential!, "***", StringComparison.Ordinal));

    private static string? Cap(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;
        var trimmed = detail.Trim();
        return trimmed.Length <= ConnectionTestResult.MaxDetailLength
            ? trimmed
            : trimmed[..(ConnectionTestResult.MaxDetailLength - 1)] + "…";
    }
}
