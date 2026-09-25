using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Reads a <see cref="GitRemoteProbe"/> as a <see cref="ConnectionTestResult"/>.
/// git says why it failed only in prose that varies by transport, server and
/// version, so a failure is matched against known phrasings in a fixed order —
/// credentials first, because a forge that hides private repositories answers a
/// bad key with "not found" alongside "authentication failed" — and anything
/// unrecognised is an <see cref="ConnectionTestOutcome.Error"/> that still
/// carries git's stderr.
/// </summary>
internal static class GitRemoteDiagnosis
{
    private static readonly string[] AuthFailures =
    [
        "authentication failed",
        "could not read username",
        "could not read password",
        "invalid username or password",
        "invalid username or token",
        "http basic: access denied",
        "returned error: 401",
        "permission denied (publickey)",
    ];

    private static readonly string[] AccessDenials = ["returned error: 403"];

    private static readonly string[] NotFounds =
    [
        "repository not found",
        "not found",
        "does not appear to be a git repository",
        "returned error: 404",
    ];

    private static readonly string[] Unreachables =
    [
        "could not resolve host",
        "failed to connect",
        "connection refused",
        "connection timed out",
        "operation timed out",
        "network is unreachable",
        "ssl certificate problem",
        "server certificate verification failed",
    ];

    public static ConnectionTestResult Classify(GitRemoteProbe probe, string? branch, bool hadApiKey, bool hasProvider)
    {
        switch (probe.ExitCode)
        {
            case 0:
                return new ConnectionTestResult(
                    ConnectionTestOutcome.Ok,
                    branch is null ? "Reached the repository." : $"Reached the repository and its default branch '{branch}'.",
                    null);
            case 2:
                return new ConnectionTestResult(
                    ConnectionTestOutcome.BranchMissing,
                    branch is null
                        ? "Reached the repository, but it has no default branch (is it empty?)."
                        : $"Reached the repository, but its default branch '{branch}' does not exist there.",
                    probe.StdErr);
        }

        var stderr = probe.StdErr;
        if (Mentions(stderr, AuthFailures))
            return new ConnectionTestResult(
                hadApiKey ? ConnectionTestOutcome.InvalidApiKey : ConnectionTestOutcome.MissingApiKey,
                hadApiKey
                    ? "The git server rejected the remote provider's API key."
                    : hasProvider
                        ? "The git server asked for credentials, but the remote provider has no API key set."
                        : "The git server asked for credentials, but the repository has no remote provider to supply them.",
                stderr);
        if (Mentions(stderr, AccessDenials))
            return new ConnectionTestResult(
                ConnectionTestOutcome.AccessDenied,
                "The git server accepted the API key but refused access to the repository.",
                stderr);
        if (Mentions(stderr, NotFounds))
            return new ConnectionTestResult(
                ConnectionTestOutcome.NotFound,
                "The repository does not exist, or the API key cannot see it.",
                stderr);
        if (Mentions(stderr, Unreachables))
            return new ConnectionTestResult(ConnectionTestOutcome.Unreachable, "Could not reach the git server.", stderr);

        return new ConnectionTestResult(ConnectionTestOutcome.Error, $"git could not read the repository (exit code {probe.ExitCode}).", stderr);
    }

    private static bool Mentions(string stderr, string[] phrases)
        => phrases.Any(phrase => stderr.Contains(phrase, StringComparison.OrdinalIgnoreCase));
}
