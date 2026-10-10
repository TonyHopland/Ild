using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations;

/// <summary>The text of chat titles: the first-message fallback, the title prompt, and the model's answer cleaned up.</summary>
public static partial class ChatTitles
{
    public const int MaxLength = 60;
    // The stored name's column length; a cut first message keeps one character less, for its ellipsis.
    public const int MaxFallbackLength = 120;
    public const int MaxFirstMessageLength = 4000;
    public const int MaxFirstReplyLength = 1500;

    private const string NoTitle = "New chat";

    /// <summary>The first message as a title (ADR-0013): plain text, at most <see cref="MaxFallbackLength"/> characters, ellipsis included.</summary>
    public static string Fallback(string firstMessage)
    {
        var text = string.Join(' ', PlainLines(firstMessage));
        if (text.Length == 0) return NoTitle;
        return text.Length <= MaxFallbackLength ? text : Prefix(text, MaxFallbackLength - 1).TrimEnd() + "…";
    }

    /// <summary>A model's answer as a title, or null when nothing usable is left.</summary>
    public static string? CleanGenerated(string output)
    {
        var line = PlainLines(output).FirstOrDefault();
        if (line is null) return null;

        string previous;
        do
        {
            previous = line;
            line = line.Trim().TrimEnd('.');
            if (line.Length >= 2 && IsQuote(line[0]) && IsQuote(line[^1]))
                line = line[1..^1];
        }
        while (line != previous);

        if (line.Length > MaxLength)
        {
            var lastSpace = line.LastIndexOf(' ', MaxLength);
            line = (lastSpace > 0 ? line[..lastSpace] : Prefix(line, MaxLength)).TrimEnd().TrimEnd('.');
        }

        return line.Length == 0 ? null : line;
    }

    /// <summary>The title model's prompt; nothing after the first successful exchange is sent.</summary>
    public static string BuildPrompt(string firstMessage, string firstReply, string? workItemTitle)
    {
        var prompt = new StringBuilder()
            .AppendLine("Write a short title for the chat below, so its user can recognise it in a list of chats.")
            .AppendLine("Rules:")
            .AppendLine("- 3 to 7 words, at most 60 characters.")
            .AppendLine("- In the language the conversation is written in.")
            .AppendLine("- Describe the topic, not the phrasing: \"Chat session sidebar with unread markers\", not \"Question about chat\".")
            .AppendLine("- No quotes and no trailing period.")
            .AppendLine("- Answer from the text below alone, and reply with the title only.");

        if (!string.IsNullOrWhiteSpace(workItemTitle))
            prompt.AppendLine().AppendLine($"The chat is about the work item \"{workItemTitle.Trim()}\".");

        return prompt
            .AppendLine()
            .AppendLine("First message:")
            .AppendLine(Cap(firstMessage, MaxFirstMessageLength))
            .AppendLine()
            .AppendLine("Start of the first reply:")
            .Append(Cap(firstReply, MaxFirstReplyLength))
            .ToString();
    }

    private static string Cap(string text, int length) => text.Length <= length ? text : Prefix(text, length);

    // Whole text elements only, so a cut never splits a surrogate pair; measured in
    // UTF-16 units, as the stored name's length is.
    private static string Prefix(string text, int maxLength)
    {
        var end = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var next = elements.ElementIndex + elements.GetTextElement().Length;
            if (next > maxLength) break;
            end = next;
        }
        return text[..end];
    }

    // Titles are written by conditional updates, past AppDbContext's save-time NUL
    // scrub, and PostgreSQL text cannot hold NUL.
    public static string WithoutNul(string text) => AppDbContext.WithoutNul(text);

    private static bool IsQuote(char c) => c is '"' or '\'' or '`' or '“' or '”' or '‘' or '’' or '«' or '»';

    /// <summary>The non-empty lines of <paramref name="markdown"/> as plain text, without NUL.</summary>
    private static IEnumerable<string> PlainLines(string markdown)
    {
        foreach (var raw in WithoutNul(markdown).Split('\n'))
        {
            if (CodeFence().IsMatch(raw)) continue;

            var line = BlockMarkers().Replace(raw, "");
            line = Image().Replace(line, "$1");
            line = Link().Replace(line, "$1");
            line = PairedMarkers().Replace(line, "");
            line = StarEmphasis().Replace(line, "$1");
            line = UnderscoreEmphasis().Replace(line, "$1");
            line = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            if (line.Length > 0) yield return line;
        }
    }

    [GeneratedRegex(@"^\s*(```|~~~)")]
    private static partial Regex CodeFence();

    // Blockquote, then heading, then a bullet or ordered-list marker, then a task box.
    [GeneratedRegex(@"^\s*(?:>\s*)*(?:#{1,6}(?:\s+|$))?(?:(?:[-*+]|\d+[.)])\s+)?(?:\[[ xX]\]\s+)?")]
    private static partial Regex BlockMarkers();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\*\*|__|~~|`")]
    private static partial Regex PairedMarkers();

    [GeneratedRegex(@"(?<![\w*])\*(?=\S)(.+?)(?<=\S)\*(?![\w*])")]
    private static partial Regex StarEmphasis();

    [GeneratedRegex(@"(?<!\w)_(?=\S)(.+?)(?<=\S)_(?!\w)")]
    private static partial Regex UnderscoreEmphasis();
}
