namespace ILD.Core.Services.Implementations.PackageFeeds;

/// <summary>
/// An Azure Artifacts feed, organization- or project-scoped, as either host names
/// it: <c>https://pkgs.dev.azure.com/{org}[/{project}]/_packaging/{feed}</c> or the
/// legacy <c>https://{org}.pkgs.visualstudio.com[/{project}]/_packaging/{feed}</c>.
/// Both are the same feed behind the same PAT, so whichever form was entered, the
/// credentials are handed out for both.
/// </summary>
public sealed class AzureFeedUrl
{
    private const string Host = "pkgs.dev.azure.com";
    private const string LegacyHostSuffix = ".pkgs.visualstudio.com";
    private const string PackagingSegment = "_packaging";

    private AzureFeedUrl(string url, string organization, string? project, string feed)
    {
        Url = url;
        Organization = organization;
        Project = project;
        Feed = feed;
    }

    /// <summary>The URL as entered, without a trailing slash.</summary>
    public string Url { get; }

    public string Organization { get; }
    public string? Project { get; }
    public string Feed { get; }

    /// <summary>The NuGet v3 service index on the host the URL names, which the feed's Test button asks.</summary>
    public string NuGetServiceIndex => $"{Url}/nuget/v3/index.json";

    /// <summary>
    /// The registry paths Azure documents npm credentials for, on both hosts,
    /// without a scheme, as npm keys them in an npmrc.
    /// </summary>
    public IReadOnlyList<string> NpmRegistryPrefixes =>
        FeedLocations.SelectMany(location => new[] { $"//{location}/npm/registry/", $"//{location}/npm/" }).ToList();

    /// <summary>The feed's NuGet service index on both hosts a <c>nuget.config</c> may name it by.</summary>
    public IReadOnlyList<string> NuGetEndpoints =>
        FeedLocations.Select(location => $"https://{location}/nuget/v3/index.json").ToList();

    // Host and path of the feed on each host; npm lowercases a host but not a path.
    private IEnumerable<string> FeedLocations
    {
        get
        {
            var feedPath = Project is null
                ? $"{PackagingSegment}/{Feed}"
                : $"{Project}/{PackagingSegment}/{Feed}";
            yield return $"{Host}/{Organization}/{feedPath}";
            yield return $"{Organization.ToLowerInvariant()}{LegacyHostSuffix}/{feedPath}";
        }
    }

    public override string ToString() => Url;

    /// <summary>
    /// Accepts exactly the feed URL shapes on either host, with one trailing slash
    /// tolerated; anything else — another scheme or host, a port, credentials, a
    /// query or fragment, a missing or extra path segment — is refused with the reason.
    /// </summary>
    public static bool TryParse(string? value, out AzureFeedUrl? url, out string? problem)
    {
        url = null;
        const string shape = $"Feed URL must look like https://{Host}/{{organization}}[/{{project}}]/_packaging/{{feed}} "
            + $"or https://{{organization}}{LegacyHostSuffix}[/{{project}}]/_packaging/{{feed}}.";

        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
        {
            problem = shape;
            return false;
        }
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            problem = "Feed URL must not have a query or fragment.";
            return false;
        }

        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath[..^1] : uri.AbsolutePath;
        var parts = path.Split('/')[1..];
        if (parts.Any(part => part.Length == 0))
        {
            problem = shape;
            return false;
        }

        // The legacy host carries the organization; the current one leads the path with it.
        string organization;
        if (string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase) && parts.Length > 0)
        {
            organization = parts[0];
            parts = parts[1..];
        }
        else if (uri.Host.EndsWith(LegacyHostSuffix, StringComparison.OrdinalIgnoreCase)
            && uri.Host[..^LegacyHostSuffix.Length] is { Length: > 0 } label && !label.Contains('.'))
        {
            organization = label;
        }
        else
        {
            problem = shape;
            return false;
        }

        // What is left is [{project}/]_packaging/{feed}.
        var shaped = organization != PackagingSegment && parts.Length switch
        {
            2 => parts[0] == PackagingSegment && parts[1] != PackagingSegment,
            3 => parts[0] != PackagingSegment && parts[1] == PackagingSegment && parts[2] != PackagingSegment,
            _ => false,
        };
        if (!shaped)
        {
            problem = shape;
            return false;
        }

        problem = null;
        url = new AzureFeedUrl(
            $"https://{uri.Host}{path}",
            organization,
            parts.Length == 3 ? parts[0] : null,
            parts[^1]);
        return true;
    }
}
