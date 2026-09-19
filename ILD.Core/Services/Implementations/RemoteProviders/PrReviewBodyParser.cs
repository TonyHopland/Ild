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

    private static readonly Regex FilesReviewed = new(
        @"Files reviewed:\*{0,2}\s*(?<seen>\d+)\s*/\s*(?<total>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The marker the reviewer opens with when its own spend guard cut it short.
    /// Load-bearing: a truncated review carries no "Files reviewed" trailer to
    /// compare, so this is the only thing distinguishing it from a quiet one.
    /// </summary>
    private const string CutShortNote = "unable to run its full agentic suite";

    /// <summary>The findings <paramref name="review"/>'s body carries without surfacing them as threads.</summary>
    public static IReadOnlyList<RemotePrReviewItem> Suppressed(RemotePrReviewSummary review)
    {
        var items = new List<RemotePrReviewItem>();
        if (string.IsNullOrWhiteSpace(review.Body))
            return items;

        var lines = review.Body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
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
