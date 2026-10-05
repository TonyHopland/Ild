using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// The text side of chat titles: the fallback a chat is named with from its first
/// message, what a title model is asked, and what its answer is reduced to before
/// it may become the title. Both titles are plain text, so the markdown a message
/// or a model writes is taken out of them the same way.
/// </summary>
public static partial class ChatTitles
{
    public const int MaxLength = 60;
    public const int MaxFirstMessageLength = 4000;
    public const int MaxFirstReplyLength = 1500;

    private const string NoTitle = "New chat";

    /// <summary>
    /// The chat's title until a better one exists (ADR-0013): the first message
    /// without its markdown, whitespace collapsed, its first <see cref="MaxLength"/>
    /// characters and an ellipsis when longer.
    /// </summary>
    public static string Fallback(string firstMessage)
    {
        var text = string.Join(' ', PlainLines(firstMessage));
        if (text.Length == 0) return NoTitle;
        return text.Length <= MaxLength ? text : Prefix(text, MaxLength).TrimEnd() + "…";
    }

    /// <summary>
    /// A model's answer as a title: its first line without markdown, surrounding
    /// quotes or trailing periods, cut at the last word that fits. Null when
    /// nothing is left, which counts as the model not answering.
    /// </summary>
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

    /// <summary>
    /// What the title model is asked: the rules, the work item the chat was about
    /// when it has one, and the start of the first message and of the first reply.
    /// Nothing later in the chat is sent.
    /// </summary>
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

    /// <summary>
    /// The longest start of <paramref name="text"/> that is whole characters as a
    /// reader sees them (text elements: an emoji, a letter with its accents) and at
    /// most <paramref name="maxLength"/> UTF-16 units — the measure the stored name's
    /// length is held to — so a cut never leaves half a surrogate pair behind.
    /// </summary>
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

    /// <summary>
    /// <paramref name="text"/> without NUL characters, which PostgreSQL text cannot
    /// hold. Every title is written by a conditional update, past the save-time
    /// scrub in AppDbContext, so each one is cleaned here first.
    /// </summary>
    public static string WithoutNul(string text) => text.Replace("\0", string.Empty);

    private static bool IsQuote(char c) => c is '"' or '\'' or '`' or '“' or '”' or '‘' or '’' or '«' or '»';

    /// <summary>
    /// The non-empty lines of <paramref name="markdown"/> as plain text, each with
    /// its whitespace collapsed: code fences dropped (their contents kept), block
    /// markers taken off the front of each line, images and links reduced to their
    /// text, and emphasis and code markers removed. NUL characters go too; see
    /// <see cref="WithoutNul"/>.
    /// </summary>
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
