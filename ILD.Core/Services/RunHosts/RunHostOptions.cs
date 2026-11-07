namespace ILD.Core.Services.RunHosts;

/// <summary>
/// The <c>ILD_RUN_HOST</c> setting. Chosen explicitly and never detected from the environment:
/// <c>local</c> (the default), <c>container</c> or <c>kubernetes</c>.
/// </summary>
public static class RunHostOptions
{
    public const string SettingName = "ILD_RUN_HOST";

    /// <summary>What a run's pod or container may use, for both hosts that isolate runs; the older <c>ILD_K8S_*</c> names still work for kubernetes.</summary>
    public const string CpuLimitSetting = "ILD_RUN_CPU_LIMIT";

    public const string MemoryLimitSetting = "ILD_RUN_MEMORY_LIMIT";

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
}
