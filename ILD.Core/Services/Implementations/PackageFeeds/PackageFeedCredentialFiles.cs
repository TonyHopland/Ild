using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Core.Services.Implementations.PackageFeeds;

/// <summary>
/// Hands a launch the credentials of its repository's selected feeds the way the
/// Azure Artifacts CI tasks do: an npm user config (<c>NPM_CONFIG_USERCONFIG</c>)
/// that authenticates every feed, and the NuGet credential provider's endpoint
/// list (<c>VSS_NUGET_EXTERNAL_FEED_ENDPOINTS</c>). Where packages come from stays
/// the repository's own <c>.npmrc</c> / <c>nuget.config</c>'s call, so no registry
/// or source is named.
///
/// <para>
/// The npm config carries the PATs, so it is written to
/// <c>AgentReadRoot/ild-package-feeds</c>: the agent can read it but cannot
/// create, rename or delete anything there, and it is neither in the worktree nor
/// in the agent home every repository shares. Each owner — one launch, or one
/// preview runtime — gets its own file and deletes it when it ends;
/// <see cref="AgentRunFiles"/> removes what a reclaimed run or a killed process left.
/// </para>
/// </summary>
public static class PackageFeedCredentialFiles
{
    public const string NpmUserConfigVariable = "NPM_CONFIG_USERCONFIG";
    public const string NuGetEndpointsVariable = "VSS_NUGET_EXTERNAL_FEED_ENDPOINTS";

    private const string DirectorySegment = "ild-package-feeds";
    private const string NpmEmail = "npm@ild.local";
    private const string NuGetUsername = "ild";

    /// <summary>
    /// Write the npm config for <paramref name="feeds"/> and return the variables
    /// that point a process at them; the file is named
    /// <c>{prefix}-{unique}.npmrc</c> (a run id prefix is what lets reclaiming the
    /// run find it). No feeds writes nothing and sets nothing.
    /// </summary>
    public static PackageFeedEnvironment Materialize(
        IReadOnlyList<PackageFeedCredential> feeds, string prefix, ILogger? logger = null)
    {
        if (feeds.Count == 0)
            return PackageFeedEnvironment.None;

        var path = Path.Combine(
            AgentIsolation.CreateAgentReadDirectory(DirectorySegment),
            $"{prefix}-{Guid.NewGuid():N}.npmrc");
        AgentIsolation.WriteAgentReadableFile(path, Encoding.UTF8.GetBytes(BuildNpmrc(feeds)));
        return new PackageFeedEnvironment(
            path,
            new Dictionary<string, string>
            {
                [NpmUserConfigVariable] = path,
                [NuGetEndpointsVariable] = BuildNuGetEndpoints(feeds),
            },
            logger ?? NullLogger.Instance);
    }

    private static string BuildNpmrc(IEnumerable<PackageFeedCredential> feeds)
    {
        var npmrc = new StringBuilder();
        foreach (var feed in feeds)
        {
            var password = Convert.ToBase64String(Encoding.UTF8.GetBytes(feed.Pat));
            foreach (var prefix in feed.Url.NpmRegistryPrefixes)
            {
                npmrc.Append(prefix).Append(":username=").Append(feed.Url.Organization).Append('\n');
                npmrc.Append(prefix).Append(":_password=").Append(password).Append('\n');
                npmrc.Append(prefix).Append(":email=").Append(NpmEmail).Append('\n');
            }
        }
        return npmrc.ToString();
    }

    private static string BuildNuGetEndpoints(IEnumerable<PackageFeedCredential> feeds)
        => JsonSerializer.Serialize(new
        {
            endpointCredentials = feeds
                .SelectMany(feed => feed.Url.NuGetEndpoints.Select(endpoint => new
                {
                    endpoint,
                    username = NuGetUsername,
                    password = feed.Pat,
                }))
                .ToArray(),
        });

    /// <summary>Delete every credential file written for a loop run.</summary>
    internal static void DeleteRunFiles(Guid loopRunId)
    {
        var directory = Path.Combine(AgentIsolation.AgentReadRoot, DirectorySegment);
        if (!Directory.Exists(directory))
            return;
        foreach (var file in Directory.EnumerateFiles(directory, $"{loopRunId:N}-*.npmrc"))
            File.Delete(file);
    }

    /// <summary>
    /// The credential files written before <paramref name="writtenBeforeUtc"/>,
    /// this process's start, for the startup sweep: whatever owned them died with
    /// an earlier process. A file this process wrote has a live owner that deletes it.
    /// </summary>
    internal static IEnumerable<string> StaleFiles(string agentReadRoot, DateTime writtenBeforeUtc)
    {
        var directory = Path.Combine(agentReadRoot, DirectorySegment);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).Where(file => File.GetLastWriteTimeUtc(file) < writtenBeforeUtc)
            : [];
    }
}

/// <summary>
/// The feed variables one owner applies to the processes it starts, over anything
/// else they are given, and the credential file behind them, which
/// <see cref="Dispose"/> deletes. Empty when no feed is selected.
/// </summary>
public sealed class PackageFeedEnvironment : IDisposable
{
    public static readonly PackageFeedEnvironment None =
        new(null, new Dictionary<string, string>(), NullLogger.Instance);

    private readonly ILogger _logger;
    private int _disposed;

    internal PackageFeedEnvironment(string? filePath, IReadOnlyDictionary<string, string> environment, ILogger logger)
    {
        FilePath = filePath;
        Environment = environment;
        _logger = logger;
    }

    public string? FilePath { get; }

    public IReadOnlyDictionary<string, string> Environment { get; }

    public void Dispose()
    {
        if (FilePath is null || Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The startup sweep, and for a run's file reclaiming the run, takes it
            // later; the owner is done with it either way.
            _logger.LogWarning(ex, "Could not remove the package feed credential file {Path}", FilePath);
        }
    }
}
