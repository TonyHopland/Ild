namespace ILD.Core.Services.RunHosts;

/// <summary>
/// Where runs execute, which is not a setting: ILD started from its own image runs them in containers, or in pods when
/// that image runs in a Kubernetes cluster; ILD started any other way (<c>dotnet run</c>, as a developer or a preview of
/// ILD does) runs them as its own processes.
/// </summary>
public static class RunHostOptions
{
    /// <summary>Set by ILD's image, and only there: ILD runs from that image.</summary>
    public const string ImageMarker = "ILD_IMAGE";

    /// <summary>Set by Kubernetes in every container of a pod.</summary>
    public const string KubernetesMarker = "KUBERNETES_SERVICE_HOST";

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

    /// <summary>The host for an ILD whose environment <paramref name="environment"/> reads.</summary>
    public static RunHostKind Detect(Func<string, string?> environment)
    {
        if (string.IsNullOrWhiteSpace(environment(ImageMarker))) return RunHostKind.Local;
        return string.IsNullOrWhiteSpace(environment(KubernetesMarker)) ? RunHostKind.Container : RunHostKind.Kubernetes;
    }
}
