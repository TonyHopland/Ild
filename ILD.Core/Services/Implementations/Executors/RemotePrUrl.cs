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
}
