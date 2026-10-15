using System.Text;
using ILD.Data.DTOs;
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
    /// The save-time check for a provider whose type declares <paramref name="rules"/>:
    /// the value must split, and may not contain a bare <c>--</c> (it would move the
    /// prompt), the model flag (the Model field owns it), a flag ILD already sets
    /// for that adapter or would be undone by, or a config override of a key ILD
    /// sets. Returns the message to show, or null when the value is fine.
    /// </summary>
    public static string? Validate(ExtraCliArgsRules rules, string? raw)
    {
        var (tokens, error) = Tokenize(raw);
        if (error is not null) return "Extra CLI arguments: " + error;

        var attached = rules.AttachedShortValues;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token == "--")
                return "Extra CLI arguments: a bare -- is not allowed, because it would move the prompt.";
            if (rules.RefusedConfigKeys is { } refusedConfigKeys && ConfigOverride(tokens, ref i) is var (configFlag, value))
            {
                if (value is null)
                    return $"Extra CLI arguments: {configFlag} is not allowed without a key=value after it.";
                var key = value.Split('=')[0].Trim();
                if (refusedConfigKeys.TryGetValue(key.Split('.')[0], out var setByIld))
                    return $"Extra CLI arguments: {configFlag} {key} is not allowed, because ILD sets {setByIld} itself.";
                continue;
            }
            if (IsFlag(token, "--model", attached) || (rules.ModelHasShortFlag && IsFlag(token, "-m", attached)))
                return "Extra CLI arguments: --model is not allowed here. Set the model with the Model field instead.";
            if (rules.ReservedFlags.FirstOrDefault(flag => IsFlag(token, flag, attached)) is { } owned)
                return $"Extra CLI arguments: {owned} is not allowed, because {rules.ReservedFlagReasons?.GetValueOrDefault(owned) ?? "ILD already sets it for this provider"}.";
        }
        return null;
    }

    // The flag and value of a codex config override at tokens[i], in each form codex
    // takes: "-c v" and "--config v" (i moves past v), "--config=v", "-cv" and "-c=v".
    // The value is null when nothing follows, or a flag does, which codex will not
    // take as a value either. Null when tokens[i] is not an override.
    private static (string Flag, string? Value)? ConfigOverride(IReadOnlyList<string> tokens, ref int i)
    {
        var token = tokens[i];
        if (token is "-c" or "--config")
            return (token, i + 1 < tokens.Count && !tokens[i + 1].StartsWith('-') ? tokens[++i] : null);
        if (token.StartsWith("--config=", StringComparison.Ordinal))
            return ("--config", token["--config=".Length..]);
        if (token.StartsWith("-c", StringComparison.Ordinal) && token.Length > 2)
            return ("-c", token[2] == '=' ? token[3..] : token[2..]);
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

    private static bool IsFlag(string token, string flag, bool attachedShortValues)
        => token == flag
           || token.StartsWith(flag + "=", StringComparison.Ordinal)
           || (attachedShortValues && flag.Length == 2 && flag[0] == '-' && flag[1] != '-'
               && token.StartsWith(flag, StringComparison.Ordinal));
}
