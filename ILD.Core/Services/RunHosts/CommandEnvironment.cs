using System.Text.RegularExpressions;

namespace ILD.Core.Services.RunHosts;

/// <summary>
/// The environment a command starts with, merged by the one rule every run host follows. The caller names only the
/// variables it means to set (<see cref="RunCommand.Environment"/>, where <c>null</c> unsets one); nothing is read from
/// <see cref="System.Diagnostics.ProcessStartInfo.Environment"/>.
///
/// <para>
/// Layers apply in this order, a later one over an earlier one:
/// <list type="number">
/// <item>The run's defaults: what the host's commands start with whatever the caller says. On the local host ILD's own
/// environment without the orchestrator's secrets and the three root variables; on an isolated host nothing for a run
/// (its pod or container has its own), the chat's toolchains for a chat.</item>
/// <item>The caller's environment, secrets it set on purpose included (the gateway token).</item>
/// <item>The host-owned variables: <c>HOME</c>, <c>NPM_CONFIG_PREFIX</c> and the proxy variables. A caller cannot change them.</item>
/// </list>
/// <c>PATH</c> is not a plain layer: the entries of a caller's <c>PATH</c> go in front of the run's, and never replace it,
/// so a caller cannot hide the run's toolchains. Where the run's <c>PATH</c> is known (the defaults hold one) the result
/// is in <see cref="Variables"/>; where it is not (a run's own pod or container) the entries are returned as
/// <see cref="PathPrefix"/> for the command to put in front of the <c>PATH</c> it finds.
/// </para>
/// </summary>
public sealed record CommandEnvironment(IReadOnlyDictionary<string, string> Variables, string PathPrefix)
{
    private const string PathName = "PATH";

    /// <param name="defaults">The run's own defaults; a null value unsets.</param>
    /// <param name="caller">What the caller sets; a null value unsets.</param>
    /// <param name="hostOwned">What the host sets over both; a null value drops the variable, a caller's too.</param>
    public static CommandEnvironment Merge(
        IEnumerable<KeyValuePair<string, string?>>? defaults,
        IReadOnlyDictionary<string, string?>? caller,
        IReadOnlyDictionary<string, string?>? hostOwned)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        Apply(variables, defaults);

        string? callerPath = null;
        foreach (var (name, value) in caller ?? new Dictionary<string, string?>())
        {
            if (name == PathName) callerPath = value;
            else Apply(variables, [new(name, value)]);
        }
        Apply(variables, hostOwned);

        var runPath = variables.GetValueOrDefault(PathName);
        var held = Split(runPath);
        var entries = Split(callerPath).Where(entry => !held.Contains(entry, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        var prefix = string.Join(System.IO.Path.PathSeparator, entries);
        if (runPath is null) return new CommandEnvironment(variables, prefix);

        variables[PathName] = string.Join(System.IO.Path.PathSeparator, entries.Concat(held));
        return new CommandEnvironment(variables, "");
    }

    /// <summary>The variables of <paramref name="variables"/> as a command's environment, which may also unset.</summary>
    public static Dictionary<string, string?> Of(IEnumerable<KeyValuePair<string, string>>? variables)
        => (variables ?? []).ToDictionary(e => e.Key, e => (string?)e.Value, StringComparer.Ordinal);

    private static void Apply(Dictionary<string, string> into, IEnumerable<KeyValuePair<string, string?>>? layer)
    {
        foreach (var (name, value) in layer ?? [])
        {
            if (value is null) into.Remove(name);
            else into[name] = value;
        }
    }

    private static List<string> Split(string? path)
        => (path ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The text of a file a shell sources to set <paramref name="variables"/>: one <c>export</c> per variable, the value in
    /// single quotes. Throws for a name that is not a shell identifier.
    /// </summary>
    public static string ShellExports(IReadOnlyDictionary<string, string> variables)
    {
        var text = new System.Text.StringBuilder();
        foreach (var (name, value) in variables)
        {
            if (!Identifier.IsMatch(name))
                throw new InvalidOperationException($"'{name}' is not a name a shell can set as an environment variable");
            text.Append("export ").Append(name).Append("='").Append(value.Replace("'", "'\\''", StringComparison.Ordinal)).Append("'\n");
        }
        return text.ToString();
    }
}
