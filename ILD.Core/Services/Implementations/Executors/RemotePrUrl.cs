namespace ILD.Core.Services.Implementations.Executors;

/// <summary>
/// Helpers for reading the numeric pull-request id out of a remote PR URL.
/// Both GitHub (<c>.../pull/N</c>) and Forgejo (<c>.../pulls/N</c>) end with
/// the PR number as their last non-empty segment.
/// </summary>
internal static class RemotePrUrl
{
    public static string? ExtractPrNumber(string? prUrl)
    {
        if (string.IsNullOrEmpty(prUrl)) return null;
        var segments = prUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var seg = segments[i].TrimEnd('?', '#');
            if (seg.Length > 0 && seg.All(char.IsDigit)) return seg;
        }
        return null;
    }

    /// <summary>
    /// The repository base URL a PR URL belongs to — everything up to the
    /// <c>/pull/N</c> (GitHub) or <c>/pulls/N</c> (Forgejo) segment. Returns null
    /// when the URL carries no such segment. Feeds the provider resolver, which
    /// parses owner/repo out of the repo URL.
    /// </summary>
    public static string? ExtractRepoUrl(string? prUrl)
    {
        if (string.IsNullOrEmpty(prUrl)) return null;
        foreach (var marker in new[] { "/pulls/", "/pull/" })
        {
            var idx = prUrl.IndexOf(marker, StringComparison.Ordinal);
            if (idx > 0) return prUrl[..idx];
        }
        return null;
    }
}
