using System.Text.RegularExpressions;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Decides whether a failed AI turn was the provider interrupting the work
/// (park the run, resume later) or the work genuinely failing (follow the
/// <c>on_failure</c> edge). Every CLI adapter reports a throttle the same way —
/// as an ordinary failure whose text carries the whole signal — so one text
/// classifier buys all four adapters the same behaviour, and an adapter that
/// never learns to classify precisely still gets it (ADR-0009).
///
/// <para>
/// Where the signal lives matters: all four adapters build a non-zero exit as
/// <c>Fail($"exit={code} stderr={stderr}", response)</c>, and a provider's
/// throttle notice arrives on stdout as the agent's <em>output</em> — the real
/// case this was built from had <c>Error = "exit=1 stderr="</c> and
/// <c>Output = "You've hit your session limit · resets 9:40am (UTC)"</c>. So
/// callers pass the output first and the error second, and the first text that
/// matches a rule decides.
/// </para>
/// </summary>
public static class AiFailureClassifier
{
    /// <summary>
    /// How long one rule may spend scanning one text. A failed turn's output can
    /// be a large blob of agent narration; a pattern that backtracks on it must
    /// never stall the node it is trying to classify (it simply doesn't match).
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Ordered; first match wins. The <see cref="FailureKind.Failed"/> rules come
    /// first because they are the exceptions that the broader interruption
    /// vocabulary would otherwise swallow — a context window is the one "limit"
    /// that parking cannot help with, since resuming the same session walks back
    /// into the same wall and only relocates the dead end.
    /// </summary>
    private static readonly (Regex Pattern, FailureKind Kind)[] Rules =
    {
        // ---- genuine failures that speak the language of a limit ----
        (new Regex(@"context[ _-](?:window|length|limit)", Opts, MatchTimeout), FailureKind.Failed),
        (new Regex(@"\b(?:prompt|input|conversation|message)\b[^.\n]{0,20}\bis too long\b", Opts, MatchTimeout), FailureKind.Failed),
        (new Regex(@"\bmaximum\b[^.\n]{0,20}\bcontext\b", Opts, MatchTimeout), FailureKind.Failed),
        (new Regex(@"\bexceeds?\b[^.\n]{0,30}\bcontext\b", Opts, MatchTimeout), FailureKind.Failed),
        (new Regex(@"\btoo many tokens\b", Opts, MatchTimeout), FailureKind.Failed),

        // ---- the provider stopped us: usage / session limits ----
        (new Regex(@"\b(?:hit|reached|exceeded|out of)\b[^.\n]{0,40}\b(?:usage|session|weekly|daily|monthly|message|request)[ _-]?limit", Opts, MatchTimeout), FailureKind.Interrupted),
        (new Regex(@"\b(?:usage|session|weekly|daily|monthly|message|request)[ _-]?limits?\b[^.\n]{0,30}\b(?:reached|exceeded|hit|resets?)\b", Opts, MatchTimeout), FailureKind.Interrupted),

        // ---- rate limiting / 429 ----
        (new Regex(@"\brate[ _-]?limit(?:ed|_error)?\b[^.\n]{0,30}\b(?:exceeded|reached|hit|error|retry)\b", Opts, MatchTimeout), FailureKind.Interrupted),
        (new Regex(@"\brate[ _-]?limit(?:ed|_error)\b", Opts, MatchTimeout), FailureKind.Interrupted),
        (new Regex(@"\b429\b|\btoo many requests\b", Opts, MatchTimeout), FailureKind.Interrupted),

        // ---- provider capacity: overload / 5xx ----
        (new Regex(@"\boverloaded(?:_error)?\b", Opts, MatchTimeout), FailureKind.Interrupted),
        (new Regex(@"\b(?:internal server error|bad gateway|service unavailable|gateway time ?out|temporarily unavailable|server error)\b", Opts, MatchTimeout), FailureKind.Interrupted),
        (new Regex(@"\b(?:http|status(?:[ _-]?code)?)\b\W{0,3}(?:50[0234])\b", Opts, MatchTimeout), FailureKind.Interrupted),

        // ---- transient mid-stream connection drop ----
        (new Regex(@"\beconnreset\b|\beconnaborted\b|\betimedout\b|\bsocket hang ?up\b", Opts, MatchTimeout), FailureKind.Interrupted),
        (new Regex(@"\bconnection (?:reset|closed|aborted|error)\b", Opts, MatchTimeout), FailureKind.Interrupted),
        (new Regex(@"\b(?:premature(?:ly)? (?:close|closed|end)|unexpected (?:eof|end of (?:stream|file|json|input)))\b", Opts, MatchTimeout), FailureKind.Interrupted),
        (new Regex(@"\bstream (?:closed|disconnected|interrupted|ended unexpectedly)\b", Opts, MatchTimeout), FailureKind.Interrupted),
        (new Regex(@"\b(?:fetch failed|network error|connection timed out)\b", Opts, MatchTimeout), FailureKind.Interrupted),
    };

    /// <summary>
    /// Classify a failed turn from the texts it produced, most signal-bearing
    /// first (output, then error). Returns <see cref="FailureKind.Unknown"/> when
    /// nothing matches, which callers treat as a genuine failure: an unrecognised
    /// failure must keep behaving exactly as it did before this existed.
    /// </summary>
    public static FailureKind Classify(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (var (pattern, kind) in Rules)
            {
                try
                {
                    if (pattern.IsMatch(text)) return kind;
                }
                catch (RegexMatchTimeoutException) { /* this rule abstains */ }
            }
        }
        return FailureKind.Unknown;
    }
}
