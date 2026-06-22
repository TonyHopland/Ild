namespace ILD.Data.DTOs;

public sealed class WorktreePreviewStartRequest
{
    public string? ProfileName { get; set; }
    public bool SkipInstall { get; set; }
    public string? PublicHost { get; set; }
    public Dictionary<string, int>? PortOverrides { get; set; }
}

public sealed class WorktreePreviewResponse
{
    public bool Configured { get; set; }
    public string State { get; set; } = "notConfigured";
    public string WorktreePath { get; set; } = string.Empty;
    public string? ConfigPath { get; set; }
    public string? ProfileName { get; set; }
    public string? PublicHost { get; set; }
    public string? StateDirectory { get; set; }
    public string? Message { get; set; }
    public List<WorktreePreviewServiceResponse> Services { get; set; } = new();
}

public sealed class WorktreePreviewLogResponse
{
    public string Service { get; set; } = string.Empty;
    public string? Content { get; set; }
}

/// <summary>
/// One service's entry in the worktree's <c>ild.config.json</c>, returned as the
/// raw (pretty-printed) JSON of that service object so the Preview tab can edit it
/// in place. <see cref="Config"/> is null when the service has no config entry.
/// </summary>
public sealed class WorktreePreviewServiceConfigResponse
{
    public string Service { get; set; } = string.Empty;
    public string? Config { get; set; }
}

/// <summary>Edited JSON for a single service's <c>ild.config.json</c> entry.</summary>
public sealed class WorktreePreviewServiceConfigUpdateRequest
{
    public string Config { get; set; } = string.Empty;
}

public sealed class WorktreePreviewServiceResponse
{
    public string Name { get; set; } = string.Empty;
    public string PortAlias { get; set; } = string.Empty;
    public string Status { get; set; } = "stopped";
    public int? Port { get; set; }
    public int? SuggestedPort { get; set; }
    public string? HealthUrl { get; set; }
    public string? PublicUrl { get; set; }
    public string? LogFilePath { get; set; }
    public int? ProcessId { get; set; }
    public int? ExitCode { get; set; }
}