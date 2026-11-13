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
            return $"{text} stdout={Tail(stdout)}";
        return text;
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= TailLength ? trimmed : "…" + trimmed[^TailLength..];
    }
}
