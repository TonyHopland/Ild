using System.Text;
using System.Text.RegularExpressions;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Implementations.RemoteProviders;

/// <summary>
/// The half of a review that never becomes a thread. GitHub's Copilot reviewer
/// emits most of what it found inside the review body — on PR #158's first review
/// eight findings lived only there, invisible to every later overview — and the
/// same body is where a review the reviewer could not finish says so. Both are
/// read here, from the body alone, with no HTTP of its own.
///
/// Never throws: a reviewer's prose is not a format anyone promised, so a shape
/// this does not recognise yields what it can and the rest is simply not found.
/// </summary>
public static class PrReviewBodyParser
{
    private static readonly Regex SectionStart = new(
        @"^\s*(?:#{1,6}\s*|<summary>\s*(?:<strong>\s*)?)Suppressed comments\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Heading = new(@"^#{1,6}\s", RegexOptions.Compiled);

    /// <summary>A finding's location line, e.g. <c>**src/A.cs:57**</c>.</summary>
    private static readonly Regex Location = new(
        @"^\*\*(?<path>[^*]+):(?<line>\d+)\*\*\s*$", RegexOptions.Compiled);

    /// <summary>The section's own trailer bullets (<c>- **Files reviewed:** 25/25 …</c>), which are not prose.</summary>
    private static readonly Regex MetadataBullet = new(
        @"^[-*]\s+\*\*[^*]+:\*\*", RegexOptions.Compiled);

    /// <summary>A finding's location as the current overview writes it: <c>`src/A.cs:57`</c>.</summary>
    private static readonly Regex OverviewLocation = new(
        @"^`(?<path>[^`:]+):(?<line>\d+)`$", RegexOptions.Compiled);

    /// <summary>Any HTML tag, which a summary is mostly made of.</summary>
    private static readonly Regex Markup = new(@"<[^>]*>", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// The zero-width spaces GitHub sprinkles through a path so it wraps. They
    /// are invisible, so leaving them in would make the same finding hash
    /// differently depending on where the reviewer let the line break.
    /// </summary>
    private static readonly Regex Invisible = new(@"[\u200b\u200c\u200d\ufeff]", RegexOptions.Compiled);

    /// <summary>How far a <c>&lt;summary&gt;</c> may run before it is not one.</summary>
    private const int MaxSummaryLines = 6;

    private static readonly Regex FilesReviewed = new(
        @"Files reviewed:\*{0,2}\s*(?<seen>\d+)\s*/\s*(?<total>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The marker the reviewer opens with when its own spend guard cut it short.
    /// Load-bearing: a truncated review carries no "Files reviewed" trailer to
    /// compare, so this is the only thing distinguishing it from a quiet one.
    /// </summary>
    private const string CutShortNote = "unable to run its full agentic suite";

    /// <summary>
    /// The findings <paramref name="review"/>'s body carries without surfacing
    /// them as threads, in either shape the reviewer has used.
    ///
    /// Two, because the format is not a contract: PR #158's reviews put them
    /// under a "Suppressed comments" heading, and the current overview
    /// (<c>ccr-overview-v2</c>) has no such heading at all — it nests each one
    /// in its own <c>&lt;details&gt;</c> under a section it renames freely
    /// ("Previously missed (3)"). Matching the heading found nothing in the
    /// newer shape, so the second reader keys on the thing that is actually
    /// stable: a collapsible whose first line is a location.
    /// </summary>
    public static IReadOnlyList<RemotePrReviewItem> Suppressed(RemotePrReviewSummary review)
    {
        if (string.IsNullOrWhiteSpace(review.Body))
            return Array.Empty<RemotePrReviewItem>();

        var lines = review.Body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var found = FromSuppressedSection(lines, review).Concat(FromOverviewDetails(lines, review));

        // One finding written in both shapes is still one finding.
        return found
            .GroupBy(i => (i.Path, i.Line, i.Body))
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>The older shape: a "Suppressed comments" section of <c>**path:line**</c> headings.</summary>
    private static IEnumerable<RemotePrReviewItem> FromSuppressedSection(string[] lines, RemotePrReviewSummary review)
    {
        var items = new List<RemotePrReviewItem>();
        var index = Array.FindIndex(lines, line => SectionStart.IsMatch(line));
        if (index < 0)
            return items;

        for (index++; index < lines.Length; index++)
        {
            var line = lines[index];
            if (EndsSection(line))
                break;

            var location = Location.Match(line);
            // A line number too large to be one is not a location: this parser
            // promises never to throw, and int.Parse on a reviewer's prose is
            // the one place that promise could be broken.
            if (!location.Success || !int.TryParse(location.Groups["line"].Value, out var lineNumber))
                continue;

            var body = ReadBody(lines, ref index);
            if (body.Length == 0)
                continue;

            items.Add(new RemotePrReviewItem(
                "suppressed",
                CommentId: null,
                ThreadId: null,
                ReviewId: review.Id,
                Path: location.Groups["path"].Value.Trim(),
                Line: lineNumber,
                Body: body,
                Author: review.Author,
                Commit: review.HeadSha,
                CreatedAt: review.SubmittedAt,
                Resolved: false,
                PostedByIld: false));
        }

        return items;
    }

    /// <summary>
    /// The current overview's shape: each body-only finding is its own
    /// <c>&lt;details&gt;</c>, headlined by a <c>&lt;summary&gt;</c> and opening
    /// with the place it is about in backticks.
    ///
    /// Deliberately keyed on that location line rather than on the section
    /// enclosing it. The sections are prose the reviewer rewrites — "Previously
    /// missed (3)", "Suppressed comments (8)" — while a collapsible whose first
    /// line is <c>`path:line`</c> is the finding itself. It also keeps the
    /// other collapsibles out without naming them: "Open" and "Resolved since
    /// last review" list findings that DID become threads, as links with no
    /// location line, and those already reach the ledger with ids of their own.
    /// </summary>
    private static IEnumerable<RemotePrReviewItem> FromOverviewDetails(string[] lines, RemotePrReviewSummary review)
    {
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].TrimStart().StartsWith("<details", StringComparison.OrdinalIgnoreCase))
                continue;

            var headline = ReadSummary(lines, index, out var afterSummary);
            if (headline is null)
                continue;

            var at = afterSummary;
            while (at < lines.Length && lines[at].Trim().Length == 0)
                at++;
            if (at >= lines.Length)
                continue;

            var location = OverviewLocation.Match(Invisible.Replace(lines[at].Trim(), string.Empty));
            // Same promise as the older reader: a number too large to be a line
            // is not a location, and nothing here may throw on a reviewer's prose.
            if (!location.Success || !int.TryParse(location.Groups["line"].Value, out var lineNumber))
                continue;

            var prose = new StringBuilder(headline);
            for (var body = at + 1; body < lines.Length; body++)
            {
                var trimmed = lines[body].Trim();
                if (trimmed.StartsWith("</details>", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("<details", StringComparison.OrdinalIgnoreCase))
                    break;
                if (trimmed.Length == 0 && prose.Length == 0)
                    continue;
                prose.Append('\n').Append(trimmed);
            }

            yield return new RemotePrReviewItem(
                "suppressed",
                CommentId: null,
                ThreadId: null,
                ReviewId: review.Id,
                Path: Invisible.Replace(location.Groups["path"].Value, string.Empty).Trim(),
                Line: lineNumber,
                Body: Invisible.Replace(prose.ToString(), string.Empty).Trim(),
                Author: review.Author,
                Commit: review.HeadSha,
                CreatedAt: review.SubmittedAt,
                Resolved: false,
                PostedByIld: false);
        }
    }

    /// <summary>
    /// The text of the <c>&lt;summary&gt;</c> opening a collapsible, with its
    /// markup taken out — the severity badge alone is a paragraph of
    /// <c>&lt;picture&gt;</c>. Handles the wrapper written across lines and the
    /// same thing on one, which is the shape Copilot's own accepted format
    /// documents. Reports the line after the one that closed it.
    /// </summary>
    private static string? ReadSummary(string[] lines, int start, out int afterSummary)
    {
        afterSummary = start + 1;
        var text = new StringBuilder();
        for (var index = start; index < lines.Length && index - start < MaxSummaryLines; index++)
        {
            text.Append(lines[index]).Append(' ');
            if (!lines[index].Contains("</summary>", StringComparison.OrdinalIgnoreCase))
                continue;

            afterSummary = index + 1;
            var whole = text.ToString();
            var opened = whole.IndexOf("<summary", StringComparison.OrdinalIgnoreCase);
            var closed = whole.IndexOf("</summary>", StringComparison.OrdinalIgnoreCase);
            if (opened < 0 || closed < opened)
                return null;
            var inner = whole[(whole.IndexOf('>', opened) + 1)..closed];
            var stripped = Whitespace.Replace(Markup.Replace(inner, " "), " ").Trim();
            return stripped.Length == 0 ? null : stripped;
        }

        return null;
    }

    /// <summary>Whether the reviewer said it could not finish this review — see <see cref="CutShortNote"/>.</summary>
    public static bool IsIncomplete(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        if (body.Contains(CutShortNote, StringComparison.OrdinalIgnoreCase))
            return true;

        var reviewed = FilesReviewed.Match(body);
        return reviewed.Success
            && int.TryParse(reviewed.Groups["seen"].Value, out var seen)
            && int.TryParse(reviewed.Groups["total"].Value, out var total)
            && seen < total;
    }

    private static bool EndsSection(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("</details>", StringComparison.OrdinalIgnoreCase)
            || Heading.IsMatch(trimmed);
    }

    /// <summary>
    /// The prose under a location line, up to whatever ends it: the next
    /// location, the fenced excerpt of the code it is about (dropped — kept, it
    /// would double the batch and read as a second finding), the section's own
    /// trailer, or the end of the section. Leaves <paramref name="index"/> on the last line
    /// consumed, so the caller's loop reconsiders the line that stopped it.
    /// </summary>
    private static string ReadBody(string[] lines, ref int index)
    {
        var body = new StringBuilder();
        while (index + 1 < lines.Length)
        {
            var line = lines[index + 1];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("**", StringComparison.Ordinal)
                || trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith('<')
                || MetadataBullet.IsMatch(trimmed)
                || EndsSection(line))
                break;

            index++;
            if (trimmed.Length == 0 && body.Length == 0)
                continue;

            if (body.Length > 0) body.Append('\n');
            body.Append(StripBullet(trimmed));
        }

        return body.ToString().Trim();
    }

    private static string StripBullet(string line)
        => (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            ? line[2..]
            : line;
}
