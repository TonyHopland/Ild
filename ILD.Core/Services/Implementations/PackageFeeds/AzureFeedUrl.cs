namespace ILD.Core.Services.Implementations.PackageFeeds;

/// <summary>
/// An Azure Artifacts feed, as <c>https://pkgs.dev.azure.com/{org}/_packaging/{feed}</c>
/// (organization-scoped) or <c>https://pkgs.dev.azure.com/{org}/{project}/_packaging/{feed}</c>
/// (project-scoped), and the addresses npm and NuGet reach that same feed at.
/// </summary>
public sealed class AzureFeedUrl
{
    private const string Host = "pkgs.dev.azure.com";
    private const string PackagingSegment = "_packaging";

    private AzureFeedUrl(string organization, string? project, string feed)
    {
        Organization = organization;
        Project = project;
        Feed = feed;
    }

    public string Organization { get; }
    public string? Project { get; }
    public string Feed { get; }

    private string FeedPath => Project is null
        ? $"{Organization}/{PackagingSegment}/{Feed}"
        : $"{Organization}/{Project}/{PackagingSegment}/{Feed}";

    /// <summary>The canonical form: no trailing slash.</summary>
    public string Url => $"https://{Host}/{FeedPath}";

    /// <summary>The NuGet v3 service index, which is also what the feed's Test button asks.</summary>
    public string NuGetServiceIndex => $"{Url}/nuget/v3/index.json";

    /// <summary>
    /// The registry paths Azure documents npm credentials for, without a scheme,
    /// as npm keys them in an npmrc.
    /// </summary>
    public IReadOnlyList<string> NpmRegistryPrefixes =>
    [
        $"//{Host}/{FeedPath}/npm/registry/",
        $"//{Host}/{FeedPath}/npm/",
    ];

    /// <summary>
    /// The feed's NuGet service index on both hosts a <c>nuget.config</c> may name
    /// it by: <c>pkgs.dev.azure.com</c> and the legacy <c>{org}.pkgs.visualstudio.com</c>.
    /// </summary>
    public IReadOnlyList<string> NuGetEndpoints =>
    [
        NuGetServiceIndex,
        Project is null
            ? $"https://{Organization}.pkgs.visualstudio.com/{PackagingSegment}/{Feed}/nuget/v3/index.json"
            : $"https://{Organization}.pkgs.visualstudio.com/{Project}/{PackagingSegment}/{Feed}/nuget/v3/index.json",
    ];

    public override string ToString() => Url;

    /// <summary>
    /// Accepts exactly the two feed URL shapes, with one trailing slash tolerated;
    /// anything else — another scheme or host, a port, credentials, a query or
    /// fragment, a missing or extra path segment — is refused with the reason.
    /// </summary>
    public static bool TryParse(string? value, out AzureFeedUrl? url, out string? problem)
    {
        url = null;
        problem = Problem(value?.Trim(), out var segments);
        if (problem is not null)
            return false;

        url = segments!.Length == 3
            ? new AzureFeedUrl(segments[0], null, segments[2])
            : new AzureFeedUrl(segments[0], segments[1], segments[3]);
        return true;
    }

    private static string? Problem(string? value, out string[]? segments)
    {
        segments = null;
        const string shape = $"Feed URL must look like https://{Host}/{{organization}}/_packaging/{{feed}} "
            + $"or https://{Host}/{{organization}}/{{project}}/_packaging/{{feed}}.";

        if (string.IsNullOrEmpty(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return shape;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0
            || !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase))
            return shape;
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return "Feed URL must not have a query or fragment.";

        var path = uri.AbsolutePath;
        if (path.EndsWith('/'))
            path = path[..^1];
        var parts = path.Split('/')[1..];
        if (parts.Any(part => part.Length == 0))
            return shape;

        var shaped = parts.Length switch
        {
            3 => parts[0] != PackagingSegment && parts[1] == PackagingSegment && parts[2] != PackagingSegment,
            4 => parts[0] != PackagingSegment && parts[1] != PackagingSegment
                && parts[2] == PackagingSegment && parts[3] != PackagingSegment,
            _ => false,
        };
        if (!shaped)
            return shape;

        segments = parts;
        return null;
    }
}
