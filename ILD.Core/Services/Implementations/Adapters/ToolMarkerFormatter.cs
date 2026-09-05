using System.Text;
using System.Text.Json;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Renders the single-line <c>[tool: Bash] npm test</c> marker that CLI adapters
/// push to the live view for every tool call. The argument summary is picked
/// generically from the tool's own JSON arguments — no adapter knows what a given
/// tool's parameters mean — and is condensed hard: the live stream is a terminal
/// view, and the per-run progress buffer evicts oldest-first at 512 KB, so a
/// marker must never cost more than one short line however large the argument
/// (a heredoc, a written file's whole content) is.
/// </summary>
internal static class ToolMarkerFormatter
{
    /// <summary>Upper bound on the rendered argument, ellipsis included.</summary>
    private const int MaxArgumentLength = 120;

    /// <summary>Argument names that identify what a call is doing, most telling
    /// first. Every CLI's built-in tools draw from roughly this vocabulary, but
    /// each spells it its own way — claude-code's <c>file_path</c> is opencode's
    /// <c>filePath</c> — so names are matched with case and underscores ignored
    /// and the entries here are already in that normalized form.</summary>
    private static readonly string[] PreferredKeys =
        ["command", "filepath", "path", "pattern", "url", "query", "description"];

    /// <param name="arguments">The tool's arguments object. Anything else — a
    /// missing element, a scalar, an array — falls back to the bare marker.</param>
    public static string Format(string? toolName, JsonElement arguments = default)
    {
        var name = string.IsNullOrWhiteSpace(toolName) ? null : toolName.Trim();
        var argument = SummarizeArguments(arguments);
        var marker = name is null ? "[tool]" : $"[tool: {name}]";
        return argument.Length == 0 ? marker : $"{marker} {argument}";
    }

    private static string SummarizeArguments(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.String)
            return Condense(arguments.GetString());

        if (arguments.ValueKind != JsonValueKind.Object)
            return string.Empty;

        // Candidates are chosen by name and only then read: a `content` argument
        // can hold a whole written file, and reading it just to keep 120
        // characters would materialise the file on every tool call.
        foreach (var key in PreferredKeys)
            foreach (var property in arguments.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String || !NameMatches(property.Name, key))
                    continue;

                var value = Condense(property.Value.GetString());
                if (value.Length > 0)
                    return value;
            }

        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            var value = Condense(property.Value.GetString());
            if (value.Length > 0)
                return value;
        }

        return string.Empty;
    }

    /// <param name="normalizedKey">An entry of <see cref="PreferredKeys"/>: already
    /// lowercase and free of underscores, which is how <paramref name="name"/> is
    /// read as it is compared.</param>
    private static bool NameMatches(string name, string normalizedKey)
    {
        var matched = 0;
        foreach (var ch in name)
        {
            if (ch == '_')
                continue;
            if (matched == normalizedKey.Length || char.ToLowerInvariant(ch) != normalizedKey[matched])
                return false;
            matched++;
        }

        return matched == normalizedKey.Length;
    }

    private static string Condense(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sb = new StringBuilder(Math.Min(value.Length, MaxArgumentLength));
        var pendingSpace = false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            // Counting the pending space too: appending it first and checking
            // afterwards would let a value whose collapsed whitespace lands on the
            // boundary run one character past the cap, un-ellipsised.
            if (sb.Length + (pendingSpace ? 2 : 1) > MaxArgumentLength)
                return Truncate(sb);

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string Truncate(StringBuilder sb)
    {
        var length = MaxArgumentLength - 1;
        while (length > 0 && sb[length - 1] == ' ')
            length--;

        return sb.ToString(0, length) + '…';
    }
}
