namespace ILD.Core.Services.Implementations;

/// <summary>
/// Parses the raw text of a <c>.env</c> file into key/value pairs for injection
/// into preview processes. Deliberately small and predictable — it is the one
/// place the repository's custom <c>.env</c> is interpreted, so its rules are the
/// contract the Preview tab documents to users:
/// <list type="bullet">
///   <item>Blank and whitespace-only lines are ignored.</item>
///   <item>Full-line comments (first non-whitespace character is <c>#</c>) are
///   ignored. Inline <c>#</c> is <b>not</b> treated as a comment — a <c>#</c> is a
///   legal character in a password or URL fragment, and silently truncating a
///   secret is worse than keeping a stray comment.</item>
///   <item>An optional leading <c>export </c> is stripped (so a file that doubles
///   as a shell-sourced script parses the same).</item>
///   <item>The key is everything before the first <c>=</c>, trimmed. A line with
///   no <c>=</c>, or an empty key, is ignored.</item>
///   <item>The value is everything after the first <c>=</c> (so <c>=</c> is legal
///   inside a value), trimmed of surrounding whitespace. If it is then wrapped in
///   matching single or double quotes the quotes are removed; inside double quotes
///   the common escapes <c>\n \r \t \\ \"</c> are unescaped, single quotes are
///   literal.</item>
///   <item>Later duplicate keys win.</item>
/// </list>
/// Keys are compared ordinally here (POSIX env names are case-sensitive); the
/// caller merges the result into a case-insensitive environment map.
/// </summary>
public static class DotEnvParser
{
    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Empty;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line["export ".Length..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            if (key.Length == 0)
                continue;

            result[key] = ParseValue(line[(eq + 1)..].Trim());
        }

        return result;
    }

    private static string ParseValue(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return Unescape(value[1..^1]);
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1];
        return value;
    }

    private static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0)
            return value;

        var sb = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    _ => next,
                });
            }
            else
            {
                sb.Append(value[i]);
            }
        }

        return sb.ToString();
    }
}
