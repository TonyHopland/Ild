namespace ILD.Data;

/// <summary>
/// The set of one-per-session <b>briefings</b> a chat's agent has already been
/// given, and which adapter session each was given to.
///
/// <para>
/// A briefing is any block of text that is constant for a session's life and too
/// large to re-send per turn. A chat turn resumes the provider's agent session
/// rather than replaying ILD's transcript, so every copy the per-turn preamble
/// sends stays in the agent's history — which makes a static block prepended each
/// turn cost the turn count squared, not the turn count (#27). Delivering it once
/// and recording that here is how a block escapes that; the guide-sized ones
/// should also be pullable, so an agent that has lost one from effective context
/// can fetch it back rather than go without.
/// </para>
///
/// <para>
/// Keyed by session and not by a bare "sent it" flag: an agent session that is
/// rebound or forked does not carry what an earlier one was told, so it has to be
/// briefed afresh rather than left silently without. <c>LoopAuthoring</c> is the
/// only key today; the shape is here so the next one is a constant rather than
/// another column.
/// </para>
///
/// Stored as newline-separated <c>key@sessionId</c> entries — session ids are
/// opaque tokens ([ADR-0009](../docs/adr/0009-adapter-feature-parity.md)), so the
/// split takes the FIRST separator and treats the rest as the id.
/// </summary>
public static class SessionBriefings
{
    /// <summary>The loop authoring guide, sent when a Loop Editor is open.</summary>
    public const string LoopAuthoring = "loop-authoring";

    private const char EntrySeparator = '\n';
    private const char KeySeparator = '@';

    /// <summary>
    /// Has <paramref name="key"/> been delivered into <paramref name="sessionId"/>?
    /// False when no session is bound yet: with nothing to have carried it, nothing
    /// can be holding it.
    /// </summary>
    public static bool IsDelivered(string? delivered, string key, string? sessionId)
    {
        if (string.IsNullOrEmpty(delivered) || string.IsNullOrEmpty(sessionId)) return false;

        foreach (var entry in delivered.Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var at = entry.IndexOf(KeySeparator);
            if (at <= 0) continue;
            if (entry.AsSpan(0, at).SequenceEqual(key) && entry.AsSpan(at + 1).SequenceEqual(sessionId))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Record <paramref name="key"/> as delivered into <paramref name="sessionId"/>,
    /// returning the new value to store. Any earlier session's entry for the same key
    /// is dropped — a briefing belongs to the session that received it, and keeping
    /// the stale one would only grow the column.
    /// </summary>
    public static string Record(string? delivered, string key, string sessionId)
    {
        var kept = (delivered ?? string.Empty)
            .Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(e =>
            {
                var at = e.IndexOf(KeySeparator);
                return at <= 0 || !e.AsSpan(0, at).SequenceEqual(key);
            });

        return string.Join(EntrySeparator, kept.Append($"{key}{KeySeparator}{sessionId}"));
    }
}
