using System.Net.Http;
using System.Text.RegularExpressions;

namespace ILD.Core.Services.Remote;

/// <summary>
/// Tells a temporary failure of a git push or forge API call from one that
/// will fail the same way every time. Only the former is worth waiting out.
/// Text that matches neither list counts as permanent, so an unfamiliar error
/// fails the node at once instead of burning minutes on retries.
/// </summary>
public static class RemoteFailureClassifier
{
    private static readonly string[] Permanent =
    {
        "Authentication failed", "HTTP 401", "HTTP 403", "HTTP 404", "HTTP 422",
        "non-fast-forward", "fetch first", "GH013", "refusing to allow",
        "Repository not found",
    };

    private static readonly string[] Transient =
    {
        "commit_refs_rules", "internal server error", "HTTP 429", "secondary rate limit",
        "RPC failed", "early EOF", "Connection reset", "timed out", "Could not resolve host",
    };

    private static readonly Regex ServerError =
        new(@"HTTP 5\d\d|returned error: 5\d\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RetryAfterHeader =
        new(@"Retry-After:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsTransient(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;

        // A secondary rate limit arrives as a 403; it is the one 403 that clears.
        if (Contains(error, "secondary rate limit"))
            return true;
        if (Permanent.Any(p => Contains(error, p)))
            return false;
        return ServerError.IsMatch(error) || Transient.Any(t => Contains(error, t));
    }

    /// <summary>
    /// Whether a thrown exception is a network-level failure. A timeout counts
    /// only when it was not the caller cancelling.
    /// </summary>
    public static bool IsTransient(Exception ex, CancellationToken callerToken)
        => ex switch
        {
            HttpRequestException => true,
            TaskCanceledException => !callerToken.IsCancellationRequested,
            _ => false,
        };

    /// <summary>The server's <c>Retry-After</c> in whole seconds, when the error text carries one.</summary>
    public static TimeSpan? RetryAfter(string? error)
    {
        if (error is null || RetryAfterHeader.Match(error) is not { Success: true } m
            || !int.TryParse(m.Groups[1].Value, out var seconds))
            return null;
        return TimeSpan.FromSeconds(seconds);
    }

    private static bool Contains(string text, string needle)
        => text.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
