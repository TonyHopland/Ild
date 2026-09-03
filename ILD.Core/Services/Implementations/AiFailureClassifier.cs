using System.Text.RegularExpressions;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Decides whether a failed AI turn was the provider interrupting the work
/// (park the run, resume later) or the work genuinely failing (follow the
/// <c>on_failure</c> edge), and — when the provider said so — reads back
/// <em>when</em> it will let the work continue (<see cref="TryParseResetAt"/>).
/// Both are the same job: reading provider prose nobody promised us a shape for,
/// which is why they share the timeout and the precision-first bias below.
/// Every CLI adapter reports a throttle the same way —
/// as an ordinary failure whose text carries the whole signal — so one text
/// classifier buys all four adapters the same behaviour, and an adapter that
/// never learns to classify precisely still gets it (ADR-0009).
///
/// <para>
/// Where the signal lives shapes the rules. All four adapters build a non-zero
/// exit as <c>Fail($"exit={code} stderr={stderr}", response)</c>, so the two
/// texts have very different characters:
/// </para>
/// <list type="bullet">
///   <item><b>Output</b> is whatever the agent produced. When the provider cuts
///   a turn off in-band it is the notice and nothing else — the real case this
///   was built from had <c>Output = "You've hit your session limit · resets
///   9:40am (UTC)"</c> against <c>Error = "exit=1 stderr="</c>. But when the
///   work itself failed, the same field holds a page of the agent's own
///   narration, and a coding agent narrates about HTTP status codes, dropped
///   connections and <c>file.cs:429</c> line numbers constantly.</item>
///   <item><b>Error</b> is machine text: the adapter's exit line, the CLI's
///   stderr, or a provider error message lifted out of a structured event.
///   Nothing narrates there.</item>
/// </list>
/// <para>
/// So the ambiguous vocabulary — bare status codes, 5xx phrases, connection
/// drops — is only trusted from the error text, while the output is matched
/// against notices a provider writes and an agent does not. The asymmetry costs
/// recall: a 5xx that reaches only the agent's stdout classifies as a genuine
/// failure and takes <c>on_failure</c>, exactly as it did before this existed.
/// That is the intended direction — parking a node that genuinely failed
/// silently breaks a loop's error handling, while missing an interruption only
/// leaves the previous behaviour in place.
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

    private static Regex Rule(string pattern) => new(pattern, Opts, MatchTimeout);

    /// <summary>
    /// Checked before any interruption rule, against both texts. These are the
    /// exceptions the interruption vocabulary would otherwise swallow: a context
    /// window is the one "limit" that parking cannot help with, since resuming
    /// the same session walks back into the same wall and only relocates the
    /// dead end.
    /// </summary>
    private static readonly Regex[] GenuineFailureRules =
    {
        Rule(@"context[ _-](?:window|length|limit)"),
        Rule(@"\b(?:prompt|input|conversation|message)\b[^.\n]{0,20}\bis too long\b"),
        Rule(@"\bmaximum\b[^.\n]{0,20}\bcontext\b"),
        Rule(@"\bexceeds?\b[^.\n]{0,30}\bcontext\b"),
        Rule(@"\btoo many tokens\b"),
    };

    /// <summary>
    /// Interruptions phrased the way a provider announces them to a user, and in
    /// vocabulary an agent narrating its own work does not reach for. Trusted in
    /// the agent's output as well as in the error text, because this is the shape
    /// an in-band cut-off arrives in.
    ///
    /// <para>
    /// The residual: an agent that <em>quotes</em> this vocabulary — writing
    /// "usage limit reached" into its own answer — parks a node that failed for
    /// its own reasons. That is inherent to reading the output at all, which the
    /// only real throttle we have leaves no alternative to, and it fails toward a
    /// visible park a human clears in one click rather than toward a run that
    /// keeps firing into an unreset limit.
    /// </para>
    /// </summary>
    private static readonly Regex[] NoticeRules =
    {
        // "You've hit your session limit", "Claude usage limit reached",
        // "You have exceeded your weekly limit".
        Rule(@"\b(?:hit|reached|exceeded|out of)\b[^.\n]{0,40}\b(?:usage|session|weekly|daily|monthly|message)[ _-]?limit\b"),
        Rule(@"\b(?:usage|session|weekly|daily|monthly|message)[ _-]?limits?\b[^.\n]{0,30}\b(?:reached|exceeded|hit|resets?)\b"),
        // Wire tokens: these are the provider's own identifiers, quoted verbatim
        // out of an error payload rather than written by anyone.
        Rule(@"\brate_limit_error\b|\boverloaded_error\b"),
        // "API Error: 429", "Error 429" — the number alone is not enough (a
        // stack trace or a file:line citation carries plenty of them), so it has
        // to arrive framed as an error.
        Rule(@"\b(?:api|http|provider|request|server)?\W{0,3}error\W{0,6}(?:code\W{0,6})?429\b"),
    };

    /// <summary>
    /// Interruptions that read identically to an agent describing code — "the
    /// endpoint returns 500 Internal Server Error", "handle ECONNRESET on
    /// retry", "the client got HTTP 503". Trusted only in the error text, which
    /// no agent writes into.
    /// </summary>
    private static readonly Regex[] AmbiguousInterruptionRules =
    {
        Rule(@"\b429\b|\btoo many requests\b"),
        Rule(@"\brate[ _-]?limit(?:ed)?\b[^.\n]{0,30}\b(?:exceeded|reached|hit|error|retry)\b"),
        Rule(@"\brate[ _-]?limited\b"),
        Rule(@"\boverloaded\b"),
        Rule(@"\b(?:internal server error|bad gateway|service unavailable|gateway time ?out|temporarily unavailable|server error|upstream connect error)\b"),
        Rule(@"\b(?:http|status(?:[ _-]?code)?|error)\b\W{0,6}5\d\d\b"),
        Rule(@"\beconnreset\b|\beconnaborted\b|\betimedout\b|\bsocket hang ?up\b"),
        Rule(@"\bconnection (?:reset|closed|aborted|error)\b"),
        Rule(@"\b(?:premature(?:ly)? (?:close|closed|end)|unexpected (?:eof|end of (?:stream|file|json|input)))\b"),
        Rule(@"\bstream (?:closed|disconnected|interrupted|ended unexpectedly)\b"),
        Rule(@"\b(?:fetch failed|network error|connection timed out)\b"),
    };

    /// <summary>
    /// Classify a failed turn. <paramref name="output"/> is the agent's own text
    /// and is matched against provider notices only; <paramref name="error"/> is
    /// adapter/CLI text and is matched against everything. Returns
    /// <see cref="FailureKind.Unknown"/> when nothing matches, which callers
    /// treat as a genuine failure: an unrecognised failure must keep behaving
    /// exactly as it did before this existed.
    /// </summary>
    public static FailureKind Classify(string? output, string? error)
    {
        var fromOutput = ClassifyText(output, trustAmbiguous: false);
        return fromOutput != FailureKind.Unknown
            ? fromOutput
            : ClassifyText(error, trustAmbiguous: true);
    }

    /// <summary>
    /// Classify a provider error message an adapter lifted out of a structured
    /// event (opencode's <c>{"type":"error"}</c>, claude-code's result error).
    /// That text is the provider's, not the agent's, so the full rule set
    /// applies.
    /// </summary>
    public static FailureKind ClassifyProviderMessage(string? message)
        => ClassifyText(message, trustAmbiguous: true);

    /// <summary>
    /// Longest wait this will report. A provider window is hours, not days, so a
    /// further-out answer is far more likely a misread — a 12-hour clock read in
    /// the wrong half of the day, or a time rolled to tomorrow that had in fact
    /// just passed — than a real limit. Past it the caller gets nothing and
    /// falls back to its own retry schedule, which costs one wasted attempt in
    /// the worst case, where honouring a bad parse would idle a run for a day.
    /// </summary>
    private static readonly TimeSpan MaxHonouredWait = TimeSpan.FromHours(12);

    /// <summary>
    /// When the provider says its limit resets, e.g. the <c>"resets 9:40am
    /// (UTC)"</c> a session limit ends with. Only a time the provider stamped
    /// <b>UTC</b> is read, along with a relative "try again in 20 minutes";
    /// a bare "resets 3pm" is deliberately left alone, because the zone it means
    /// is the user's and guessing wrong parks the run for hours it did not have
    /// to wait. Anything unread returns false and leaves the caller on its own
    /// schedule, which is what every throttle got before this existed.
    /// </summary>
    public static bool TryParseResetAt(string? output, string? error, DateTime nowUtc, out DateTime resetAtUtc)
        => TryParseResetAt(output, nowUtc, out resetAtUtc)
            || TryParseResetAt(error, nowUtc, out resetAtUtc);

    private static readonly Regex AbsoluteResetRule = Rule(
        @"\b(?:resets?|available again)\b(?:\s+at)?\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?\s*[\(\[]?\s*(?:utc|gmt)\b");

    private static readonly Regex RelativeResetRule = Rule(
        @"\b(?:try again|retry|resets?|available again)\s+in\s+(\d{1,4})\s*(seconds?|secs?|minutes?|mins?|hours?|hrs?)\b");

    private static bool TryParseResetAt(string? text, DateTime nowUtc, out DateTime resetAtUtc)
    {
        resetAtUtc = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var at = MatchAbsolute(text, nowUtc) ?? MatchRelative(text, nowUtc);
        if (at is null || at <= nowUtc || at - nowUtc > MaxHonouredWait) return false;

        resetAtUtc = at.Value;
        return true;
    }

    private static DateTime? MatchAbsolute(string text, DateTime nowUtc)
    {
        var m = Match(AbsoluteResetRule, text);
        if (m is null) return null;

        var hour = int.Parse(m.Groups[1].Value);
        var minute = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        if (minute > 59) return null;

        if (m.Groups[3].Success)
        {
            if (hour is < 1 or > 12) return null;
            var pm = m.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
            hour = pm ? (hour == 12 ? 12 : hour + 12) : (hour == 12 ? 0 : hour);
        }
        else if (hour > 23) return null;

        var today = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, hour, minute, 0, DateTimeKind.Utc);
        // A time already past is the same clock face coming round again — the
        // notice written at 10pm saying "resets 9:40am" means tomorrow morning.
        // The horizon above is what keeps a time that merely just elapsed from
        // buying a full day's wait.
        return today > nowUtc ? today : today.AddDays(1);
    }

    private static DateTime? MatchRelative(string text, DateTime nowUtc)
    {
        var m = Match(RelativeResetRule, text);
        if (m is null) return null;

        var amount = int.Parse(m.Groups[1].Value);
        var unit = m.Groups[2].Value.ToLowerInvariant();
        var span = unit[0] switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            _ => TimeSpan.FromHours(amount),
        };
        return nowUtc + span;
    }

    private static Match? Match(Regex rule, string text)
    {
        try
        {
            var m = rule.Match(text);
            return m.Success ? m : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static FailureKind ClassifyText(string? text, bool trustAmbiguous)
    {
        if (string.IsNullOrWhiteSpace(text)) return FailureKind.Unknown;
        if (Matches(GenuineFailureRules, text)) return FailureKind.Failed;
        if (Matches(NoticeRules, text)) return FailureKind.Interrupted;
        if (trustAmbiguous && Matches(AmbiguousInterruptionRules, text)) return FailureKind.Interrupted;
        return FailureKind.Unknown;
    }

    private static bool Matches(Regex[] rules, string text)
    {
        foreach (var rule in rules)
        {
            try
            {
                if (rule.IsMatch(text)) return true;
            }
            catch (RegexMatchTimeoutException) { /* this rule abstains */ }
        }
        return false;
    }
}
