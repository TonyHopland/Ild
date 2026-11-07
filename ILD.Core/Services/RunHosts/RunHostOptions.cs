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

    /// <summary>
    /// Whether runs get their toolchains and agents from the shared store (<c>on</c>, the default) or only what the
    /// runner image has (<c>off</c>, for a full runner image).
    /// </summary>
    public const string ToolchainsSetting = "ILD_RUN_TOOLCHAINS";

    /// <summary><see cref="ToolchainsSetting"/>: on when unset.</summary>
    /// <exception cref="InvalidOperationException">The value is neither on nor off.</exception>
    public static bool ToolchainsOn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return value.Trim().ToLowerInvariant() switch
        {
            "on" or "true" or "1" => true,
            "off" or "false" or "0" => false,
            _ => throw new InvalidOperationException($"{ToolchainsSetting} is '{value}'; it must be on or off."),
        };
    }

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
