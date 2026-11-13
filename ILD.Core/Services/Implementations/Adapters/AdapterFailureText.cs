namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// The node error for a CLI that exited non-zero. Headless CLIs report their
/// failure on stdout and leave stderr empty, so an error of just
/// <c>exit=1 stderr=</c> throws away the only explanation there is.
/// </summary>
internal static class AdapterFailureText
{
    private const int TailLength = 500;

    /// <summary>
    /// <c>exit=N stderr=…</c>, followed by <c>result=…</c> when the CLI named its
    /// error in a structured event, or by a trimmed tail of <paramref name="stdout"/>
    /// when both that and stderr are empty.
    /// </summary>
    public static string NonZeroExit(int exitCode, string stderr, string? providerError, string? stdout)
    {
        var text = $"exit={exitCode} stderr={stderr}";
        if (!string.IsNullOrWhiteSpace(providerError))
            return $"{text} result={Tail(providerError)}";
        if (string.IsNullOrWhiteSpace(stderr) && !string.IsNullOrWhiteSpace(stdout))
            return $"{text}{StdoutMarker}{Tail(stdout)}";
        return text;
    }

    private const string StdoutMarker = " stdout=";

    /// <summary>
    /// The error without the appended <c>stdout=</c> tail. That tail is the agent's own
    /// narration, so it is shown to people but never read as machine text.
    /// </summary>
    public static string? WithoutStdoutTail(string? error)
    {
        if (error is null) return null;
        var at = error.IndexOf(StdoutMarker, StringComparison.Ordinal);
        if (at < 0 || !error.StartsWith("exit=", StringComparison.Ordinal)) return error;
        var head = error[..at];
        var stderrAt = head.IndexOf("stderr=", StringComparison.Ordinal);
        return stderrAt >= 0 && string.IsNullOrWhiteSpace(head[(stderrAt + "stderr=".Length)..]) ? head : error;
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= TailLength ? trimmed : "…" + trimmed[^TailLength..];
    }
}
