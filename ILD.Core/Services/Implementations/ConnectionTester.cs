using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ILD.Core.Services.Implementations.PackageFeeds;
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

    private const string FeedUnreachable = "Azure DevOps unreachable.";

    public async Task<ConnectionTestResult> TestPackageFeedAsync(PackageFeed feed, CancellationToken ct)
    {
        var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes($"ild:{feed.Pat}"));
        ConnectionTestResult result;
        if (!AzureFeedUrl.TryParse(feed.FeedUrl, out var url, out var problem))
        {
            result = new ConnectionTestResult(ConnectionTestOutcome.Misconfigured, problem!, null);
        }
        else
        {
            result = await WithTimeoutAsync(ProviderTimeout, ct, token => ProbeFeedAsync(url!, credential, token));
            // A feed that never answered is as unreachable as one that refused the connection.
            if (result.Outcome == ConnectionTestOutcome.Unreachable)
                result = result with { Message = FeedUnreachable };
        }
        return Report(result, "Package feed", feed.Id, feed.Pat, credential);
    }

    /// <summary>
    /// One authenticated GET of the feed's NuGet service index, as the credential
    /// provider would make it: Basic auth with the PAT as the password.
    /// </summary>
    private async Task<ConnectionTestResult> ProbeFeedAsync(AzureFeedUrl url, string credential, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url.NuGetServiceIndex);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential);
        HttpStatusCode status;
        string body;
        try
        {
            using var response = await _http.SendAsync(request, ct);
            status = response.StatusCode;
            body = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(ct));
        }
        catch (HttpRequestException ex)
        {
            return new ConnectionTestResult(ConnectionTestOutcome.Unreachable, FeedUnreachable,
                ex.InnerException is null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})");
        }

        var evidence = $"HTTP {(int)status} {status}\n{body}";
        return status switch
        {
            _ when (int)status is >= 200 and < 300 => new ConnectionTestResult(ConnectionTestOutcome.Ok,
                NuGetCredentialProvider.IsMissing(ProcessEnvironment.Current)
                    ? "The PAT can read this feed, but NuGet credentials can't be delivered here: the Azure Artifacts credential provider was not found. npm feeds are unaffected."
                    : "The PAT can read this feed.",
                null),
            HttpStatusCode.Unauthorized => new ConnectionTestResult(ConnectionTestOutcome.InvalidApiKey,
                "PAT rejected, probably expired or revoked. Create a new one with Packaging (Read) and paste it here.", evidence),
            HttpStatusCode.Forbidden => new ConnectionTestResult(ConnectionTestOutcome.AccessDenied,
                "PAT has no access to this feed. Check its scope and organization.", evidence),
            HttpStatusCode.NotFound => new ConnectionTestResult(ConnectionTestOutcome.NotFound,
                "Feed not found. Check the URL.", evidence),
            _ when (int)status >= 500 => new ConnectionTestResult(ConnectionTestOutcome.Unreachable, FeedUnreachable, evidence),
            _ => new ConnectionTestResult(ConnectionTestOutcome.Error,
                $"Azure DevOps answered with HTTP {(int)status}.", evidence),
        };
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
