using ILD.Data.Entities;
using System.Text.RegularExpressions;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// The outcome of resolving one session field. <see cref="Value"/> is the
/// session name to bind against — null when the field was left unset — and
/// <see cref="Error"/> is the operator-facing reason the field could not be
/// used. Exactly one of the two is meaningful.
/// </summary>
internal readonly record struct SessionPlaceholderResolution(string? Value, string? Error);

/// <summary>
/// Owns the grammar and the run-time resolution of an AI node's session fields
/// (<c>sessionPlaceholder</c>, <c>forkFromPlaceholder</c>), so the save-time
/// validator and the executor cannot drift apart on what a session name may say
/// or what it resolves to.
///
/// A session field is a template field like a prompt, but with a deliberately
/// narrower grammar: <c>{{Var.&lt;name&gt;}}</c> only. The other placeholder
/// families are rejected at save time — <c>{{PreviousNode.Output}}</c> in a
/// session name would mint a brand-new, unbounded session on every turn, which
/// is the opposite of what naming a session is for.
///
/// Resolution is deliberately strict where the prompt pipeline is forgiving.
/// <see cref="PromptTemplateResolver"/> renders an unset <c>Var.</c> to the
/// empty string because a handoff producer may legitimately run after the
/// template that reads it; here that same leniency would silently collapse
/// every iteration of a loop back onto one shared session — the exact bug
/// templated session names exist to fix. So an unset variable, an empty render,
/// or a render the binding store cannot hold is an error the caller must
/// surface, never a value.
/// </summary>
internal static class SessionPlaceholderTemplate
{
    /// <summary>
    /// Ceiling on a resolved session name, mirroring
    /// <see cref="LoopRunSessionBinding.PlaceholderId"/>'s <c>[MaxLength(128)]</c>.
    /// The column is part of the binding's primary key, so an over-long name
    /// cannot be stored and the run would otherwise resume nothing.
    /// </summary>
    public const int MaxLength = 128;

    /// <summary>True when the field carries at least one <c>{{...}}</c>, i.e.
    /// resolving it needs the run's loop variables. A literal field does not
    /// touch the store at all.</summary>
    public static bool IsTemplated(string? value)
        => !string.IsNullOrEmpty(value) && PromptPlaceholderRegistry.Pattern.IsMatch(value);

    /// <summary>
    /// The placeholder names used in <paramref name="value"/> that a session
    /// field may not contain, in the order they appear. Empty for a literal
    /// field and for one that uses only <c>{{Var.&lt;name&gt;}}</c>. Backs the
    /// save-time check, where the author can still fix it.
    /// </summary>
    public static IReadOnlyList<string> DisallowedPlaceholders(string? value)
    {
        if (string.IsNullOrEmpty(value)) return Array.Empty<string>();
        List<string>? bad = null;
        foreach (Match m in PromptPlaceholderRegistry.Pattern.Matches(value))
        {
            var name = m.Groups[1].Value;
            if (IsLoopVariable(name)) continue;
            (bad ??= new()).Add(name);
        }
        return (IReadOnlyList<string>?)bad ?? Array.Empty<string>();
    }

    /// <summary>
    /// Resolves one session field against the run's loop variables.
    /// <paramref name="field"/> names the config key for the error message;
    /// <paramref name="variables"/> may be null when the field is not templated.
    /// An unset field resolves to a null value with no error — "no session here"
    /// is a legitimate configuration, and the save-time validator is what
    /// requires one when <c>useSession</c> is on.
    /// </summary>
    public static SessionPlaceholderResolution Resolve(
        string field, string? template, IReadOnlyDictionary<string, string>? variables)
    {
        if (string.IsNullOrWhiteSpace(template))
            return new SessionPlaceholderResolution(null, null);

        string? error = null;
        var resolved = PromptPlaceholderRegistry.Pattern.Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            if (!IsLoopVariable(name))
            {
                error ??= $"AI node {field} '{template}' may only use {{{{Var.<name>}}}} placeholders; '{{{{{name}}}}}' is not one.";
                return string.Empty;
            }
            var variable = name.Substring(PromptPlaceholderRegistry.VariablePrefix.Length);
            if (variables is not null && variables.TryGetValue(variable, out var v)) return v;
            error ??= $"AI node {field} '{template}' reads loop variable '{variable}', which is not set on this run. A session name that resolved anyway would silently share one session across iterations; set the variable upstream of this node, or use a literal session name.";
            return string.Empty;
        });

        if (error is not null)
            return new SessionPlaceholderResolution(null, error);

        // The binding store keys on this string, so a name it cannot hold means
        // the node would run and then bind nothing — a silent no-session.
        if (string.IsNullOrWhiteSpace(resolved))
            return new SessionPlaceholderResolution(
                null, $"AI node {field} '{template}' resolved to an empty session name.");
        if (resolved.Length > MaxLength)
            return new SessionPlaceholderResolution(
                null, $"AI node {field} '{template}' resolved to a {resolved.Length}-character session name; the limit is {MaxLength}.");

        return new SessionPlaceholderResolution(resolved, null);
    }

    private static bool IsLoopVariable(string name)
        => name.StartsWith(PromptPlaceholderRegistry.VariablePrefix, StringComparison.OrdinalIgnoreCase)
           && PromptPlaceholderRegistry.IsValidVariableName(
               name.Substring(PromptPlaceholderRegistry.VariablePrefix.Length));
}
