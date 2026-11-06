using System.Text;
using System.Text.RegularExpressions;

namespace ILD.Api.Services;

/// <summary>
/// Finds the long-lived token <c>claude setup-token</c> prints in a provider terminal (ADR-0031).
/// The terminal colours its text and wraps a line as wide as the terminal, so the output is read with
/// escape sequences removed, and a line that fills the terminal continues on the next. A token is
/// taken only once the line that ends it is finished.
/// </summary>
public sealed partial class SetupTokenCapture(int columns)
{
    private const int MaxBuffered = 64 * 1024;
    private const int MinTokenLength = 40;

    private readonly StringBuilder _text = new();

    public string? Token { get; private set; }

    public void Feed(ReadOnlySpan<byte> output)
    {
        if (Token is not null) return;
        _text.Append(Encoding.UTF8.GetString(output));
        if (_text.Length > MaxBuffered) _text.Remove(0, _text.Length - MaxBuffered);

        var lines = Escapes().Replace(_text.ToString(), string.Empty).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        for (var i = 0; i < lines.Length - 1; i++)
        {
            var start = TokenStart().Match(lines[i]);
            if (!start.Success) continue;

            var token = new StringBuilder(start.Value);
            var line = lines[i];
            var next = i;
            while (line.Length == columns && next + 1 < lines.Length - 1)
            {
                line = lines[++next];
                token.Append(TokenChars().Match(line).Value);
            }
            if (token.Length >= MinTokenLength)
            {
                Token = token.ToString();
                return;
            }
        }
    }

    [GeneratedRegex(@"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)|\x1b[@-_]")]
    private static partial Regex Escapes();

    [GeneratedRegex(@"sk-ant-oat\d+-[A-Za-z0-9_-]*")]
    private static partial Regex TokenStart();

    [GeneratedRegex(@"^[A-Za-z0-9_-]+")]
    private static partial Regex TokenChars();
}
