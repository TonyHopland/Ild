using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Implementations;

/// <summary>Turns a config and a runtime into the <see cref="WorktreePreviewResponse"/> every status surface reports.</summary>
internal sealed class PreviewResponses(PreviewProxyBase proxyBase)
{
    public WorktreePreviewResponse BuildResponse(LoadedPreviewConfig loaded, PreviewRuntime runtime)
    {
        // List every service in the profile — not just the ones with a live process —
        // so the Preview tab can show and individually start services that are stopped.
        // Started services map from their process; the rest report as stopped with the
        // runtime's already-allocated port.
        var profileServices = loaded.Config?.Preview?.Profiles.TryGetValue(runtime.ProfileName, out var profile) == true
            ? profile.Services
            : runtime.Processes.Select(p => p.Service).ToList();

        // The bare wi-{id} hostname names one service, so it can only be advertised
        // when the profile has exactly one public service. With more than one, each
        // is advertised under its own wi-{id}-{name} instead — otherwise both would
        // be handed the same URL and one of them would reach the wrong application.
        var singlePublicService = HasSinglePublicService(profileServices);

        var serviceResponses = profileServices.Select(service =>
        {
            var process = runtime.Processes.FirstOrDefault(p => string.Equals(p.Service.Name, service.Name, StringComparison.OrdinalIgnoreCase));
            return process != null
                ? BuildServiceResponse(process, runtime, singlePublicService)
                : BuildStoppedServiceResponse(service, runtime);
        }).ToList();

        foreach (var row in serviceResponses)
            row.DependsOn = PreviewTemplates.DependsOnOf(profileServices.First(s => s.Name == row.Name), profileServices);

        return new WorktreePreviewResponse
        {
            Configured = true,
            State = ComputeRuntimeState(serviceResponses),
            WorktreePath = runtime.WorktreePath,
            ConfigPath = loaded.ConfigPath,
            ProfileName = runtime.ProfileName,
            PublicHost = runtime.PublicHost,
            StateDirectory = runtime.StateDirectory,
            Services = serviceResponses,
        };
    }

    internal static bool HasSinglePublicService(IEnumerable<PreviewServiceConfig> services)
        => services.Count(service => service.Public) == 1;

    private WorktreePreviewServiceResponse BuildServiceResponse(ManagedPreviewProcess process, PreviewRuntime runtime, bool singlePublicService)
    {
        var healthUrl = PreviewTemplates.ResolveHealthUrl(process.Service, runtime);
        var port = runtime.Ports.TryGetValue(process.Service.Port, out var allocated) ? allocated : (int?)null;
        var publicUrl = process.Service.Public ? BuildPublicUrl(process.Service, runtime, port, singlePublicService) : null;

        return new WorktreePreviewServiceResponse
        {
            Name = process.Service.Name,
            PortAlias = process.Service.Port,
            Status = process.Process.HasExited ? "exited" : "running",
            Port = port,
            SuggestedPort = process.Service.SuggestedPort,
            HealthUrl = healthUrl,
            PublicUrl = publicUrl,
            LocalUrl = PreviewUrls.ComposeLocalUrl(port),
            LogFilePath = process.LogFilePath,
            ProcessId = process.Process.Id,
            ExitCode = process.Process.HasExited ? process.Process.ExitCode : null,
        };
    }

    /// <summary>
    /// The URL a human is handed for a <c>"public": true</c> service, in strict
    /// precedence: the service's own <c>publicUrl</c> template wins (an author who
    /// spelled out a URL means it); then the preview proxy origin, which is the
    /// only form reachable when ILD runs behind an ingress that never published the
    /// service's port; then the historical direct <c>http://{publicHost}:{port}</c>.
    /// With <c>ILD_PREVIEW_PROXY_BASE</c> unset the middle rung disappears and the
    /// result is exactly what it has always been.
    /// <para>
    /// <paramref name="singlePublicService"/> decides whether the bare
    /// <c>wi-{id}</c> hostname is this service's to claim; when it is not, the
    /// service is advertised as <c>wi-{id}-{name}</c>. A name that is not a legal
    /// DNS label cannot appear in a hostname at all, so such a service falls back
    /// to the direct URL rather than being handed one that cannot resolve.
    /// </para>
    /// </summary>
    internal string? BuildPublicUrl(PreviewServiceConfig service, PreviewRuntime runtime, int? port, bool singlePublicService)
        => PreviewUrls.ComposePublicUrl(
            PreviewTemplates.ResolveOptional(service.PublicUrl, runtime, service.Port),
            proxyBase, runtime.WorkItemId, service.Name, runtime.PublicHost, port, singlePublicService);

    // A configured-but-not-running service in an otherwise live runtime. Health and
    // public URLs are left null: their templates can reference other services' ports
    // and resolving them for a service that isn't up adds no value.
    private static WorktreePreviewServiceResponse BuildStoppedServiceResponse(PreviewServiceConfig service, PreviewRuntime runtime)
        => new()
        {
            Name = service.Name,
            PortAlias = service.Port,
            Status = "stopped",
            Port = runtime.Ports.TryGetValue(service.Port, out var port) ? port : null,
            SuggestedPort = service.SuggestedPort,
        };

    private static string ComputeRuntimeState(IReadOnlyList<WorktreePreviewServiceResponse> services)
    {
        if (services.Count == 0)
            return "stopped";
        if (services.Any(s => string.Equals(s.Status, "exited", StringComparison.OrdinalIgnoreCase)))
            return "failed";
        if (services.All(s => string.Equals(s.Status, "running", StringComparison.OrdinalIgnoreCase)))
            return "running";
        if (services.Any(s => string.Equals(s.Status, "running", StringComparison.OrdinalIgnoreCase)))
            return "partial";
        return "stopped";
    }

    public WorktreePreviewResponse BuildStoppedResponse(
        LoadedPreviewConfig loaded,
        string? profileName = null,
        string? publicHost = null,
        string? stateDirectory = null,
        string? configPath = null)
    {
        var resolvedProfileName = profileName ?? (loaded.Configured && loaded.Config != null ? PreviewConfigLoader.SelectProfileName(loaded.Config, null) : null);
        var stoppedServices = resolvedProfileName != null && loaded.Config?.Preview?.Profiles.TryGetValue(resolvedProfileName, out var profile) == true
            ? profile.Services.Select(service => new WorktreePreviewServiceResponse
            {
                Name = service.Name,
                PortAlias = service.Port,
                Status = "stopped",
                SuggestedPort = service.SuggestedPort,
                DependsOn = PreviewTemplates.DependsOnOf(service, profile.Services),
            }).ToList()
            : new List<WorktreePreviewServiceResponse>();

        return new WorktreePreviewResponse
        {
            Configured = loaded.Configured,
            State = loaded.Configured ? "stopped" : "notConfigured",
            WorktreePath = loaded.WorktreePath,
            ConfigPath = configPath ?? loaded.ConfigPath,
            ProfileName = resolvedProfileName,
            PublicHost = publicHost,
            StateDirectory = stateDirectory,
            Message = loaded.Message,
            Services = stoppedServices,
        };
    }

    /// <summary>The snapshot a start begins with: starting, and <paramref name="pending"/> services (all by default) waiting their turn.</summary>
    public WorktreePreviewResponse BuildPendingResponse(LoadedPreviewConfig loaded, string profileName, IReadOnlyCollection<string>? pending = null)
    {
        var response = BuildStoppedResponse(loaded, profileName);
        response.State = "starting";
        foreach (var row in response.Services)
        {
            if (pending == null || pending.Contains(row.Name, StringComparer.OrdinalIgnoreCase))
                row.Status = "pending";
        }

        return response;
    }

    /// <summary>
    /// The final snapshot of a start that failed: what is left once everything it launched
    /// is down. A service that failed keeps its exit code and error; the rest read stopped.
    /// </summary>
    public static void MarkFailed(WorktreePreviewResponse response, Exception failure)
    {
        response.State = "failed";
        response.Message = failure.Message;
        response.Install = null;
        foreach (var row in response.Services)
        {
            row.ProcessId = null;
            if (row.Status == "failed")
                continue;

            row.Status = "stopped";
            row.HealthUrl = null;
            row.PublicUrl = null;
            row.LocalUrl = null;
        }
    }
}
