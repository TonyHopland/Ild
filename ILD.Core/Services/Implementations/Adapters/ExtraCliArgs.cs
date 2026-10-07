using System.Text;
using ILD.Data.Entities;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>Result of splitting an "Extra CLI arguments" value: the tokens, or what is wrong with it and no tokens.</summary>
public sealed record ExtraCliArgsTokens(IReadOnlyList<string> Tokens, string? Error);

/// <summary>
/// The per-provider "Extra CLI arguments" value (<see cref="AiProviderConfig.ExtraArgs"/>).
/// It is split like a shell command line but never handed to a shell: the tokens go
/// straight into the agent's argv, so nothing is expanded, globbed or substituted.
/// </summary>
public static class ExtraCliArgs
{
    private static readonly Dictionary<string, string[]> ReservedFlagsByProviderType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-code"] = ClaudeCodeAdapter.ReservedCliFlags,
        ["copilot"] = CopilotAdapter.ReservedCliFlags,
        ["opencode"] = OpenCodeAdapter.ReservedCliFlags,
        ["pi"] = PiAdapter.ReservedCliFlags,
    };

    /// <summary>
    /// Split <paramref name="raw"/> into argv tokens. Whitespace separates tokens;
    /// <c>'…'</c> is taken literally; <c>"…"</c> groups words; a backslash outside
    /// single quotes makes the next character literal. Quoted parts join the text
    /// next to them, and <c>""</c> is an empty token. An unbalanced quote or a
    /// trailing lone backslash is an error.
    /// </summary>
    public static ExtraCliArgsTokens Tokenize(string? raw)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return new(tokens, null);

        var current = new StringBuilder();
        var inToken = false;
        char? quote = null;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (quote == '\'')
            {
                if (c == '\'') quote = null;
                else current.Append(c);
                continue;
            }

            if (c == '\\')
            {
                if (i + 1 == raw.Length)
                    return Failed("the backslash at the end has nothing to escape.");
                current.Append(raw[++i]);
                inToken = true;
                continue;
            }

            if (quote == '"')
            {
                if (c == '"') quote = null;
                else current.Append(c);
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                inToken = true;
            }
            else if (c is ' ' or '\t' or '\r' or '\n')
            {
                if (inToken) tokens.Add(current.ToString());
                current.Clear();
                inToken = false;
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (quote is { } open)
            return Failed($"the {open} quote is not closed.");
        if (inToken) tokens.Add(current.ToString());
        return new(tokens, null);

        static ExtraCliArgsTokens Failed(string error) => new(Array.Empty<string>(), error);
    }

    /// <summary>
    /// Show <paramref name="tokens"/> as one line that <see cref="Tokenize"/> splits
    /// back into the same tokens: plain tokens as they are, anything else in double
    /// quotes with <c>"</c> and <c>\</c> escaped.
    /// </summary>
    public static string Format(IEnumerable<string> tokens)
        => string.Join(' ', tokens.Select(token =>
            token.Length > 0 && !token.Any(c => char.IsWhiteSpace(c) || c is '\'' or '"' or '\\')
                ? token
                : "\"" + token.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));

    /// <summary>
    /// The save-time check for a provider of <paramref name="providerType"/>: the
    /// value must split, and may not contain a bare <c>--</c> (it would move the
    /// prompt), the model flag (the Model field owns it) or a flag ILD already sets
    /// for that adapter. Returns the message to show, or null when the value is fine.
    /// </summary>
    public static string? Validate(string providerType, string? raw)
    {
        var (tokens, error) = Tokenize(raw);
        if (error is not null) return "Extra CLI arguments: " + error;

        var isOpenCode = string.Equals(providerType, "opencode", StringComparison.OrdinalIgnoreCase);
        var reserved = ReservedFlagsByProviderType.GetValueOrDefault(providerType) ?? [];
        foreach (var token in tokens)
        {
            if (token == "--")
                return "Extra CLI arguments: a bare -- is not allowed, because it would move the prompt.";
            if (IsFlag(token, "--model") || (isOpenCode && IsFlag(token, "-m")))
                return "Extra CLI arguments: --model is not allowed here. Set the model with the Model field instead.";
            if (reserved.FirstOrDefault(flag => IsFlag(token, flag)) is { } owned)
                return $"Extra CLI arguments: {owned} is not allowed, because ILD already sets it for this provider.";
        }
        return null;
    }

    /// <summary>
    /// The tokens to launch <paramref name="provider"/>'s agent with. A stored value
    /// that does not split (saved around <see cref="Validate"/>, e.g. by an older
    /// build) is logged and dropped, so the run goes ahead without it.
    /// </summary>
    public static IReadOnlyList<string> ForLaunch(AiProvider provider, ILogger logger)
    {
        var (tokens, error) = Tokenize(AiProviderConfig.Parse(provider.Config).ExtraArgs);
        if (error is null) return tokens;

        logger.LogWarning(
            "Launching AI provider {ProviderName} ({ProviderId}) without its extra CLI arguments: {Error}",
            provider.Name, provider.Id, error);
        return tokens;
    }

    private static bool IsFlag(string token, string flag)
        => token == flag || token.StartsWith(flag + "=", StringComparison.Ordinal);
}
