namespace ILD.Core.Services.RunHosts;

/// <summary>
/// The <c>ILD_RUN_HOST</c> setting. Chosen explicitly and never detected from the environment:
/// <c>local</c> (the default), <c>container</c> or <c>kubernetes</c>.
/// </summary>
public static class RunHostOptions
{
    public const string SettingName = "ILD_RUN_HOST";

    /// <summary>The backend <paramref name="value"/> names; <see cref="RunHostKind.Local"/> when it is unset or blank.</summary>
    /// <exception cref="InvalidOperationException">The value is none of the three.</exception>
    public static RunHostKind Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return RunHostKind.Local;
        return value.Trim().ToLowerInvariant() switch
        {
            "local" => RunHostKind.Local,
            "container" => RunHostKind.Container,
            "kubernetes" => RunHostKind.Kubernetes,
            _ => throw new InvalidOperationException($"{SettingName} is '{value}'; it must be local, container or kubernetes."),
        };
    }

    /// <summary>The backend the configuration selects, failing for one that is not built yet.</summary>
    public static RunHostKind Select(string? value)
    {
        var kind = Parse(value);
        if (kind != RunHostKind.Local)
            throw new InvalidOperationException($"{SettingName}={kind.ToString().ToLowerInvariant()} is not implemented yet; only local is.");
        return kind;
    }
}
