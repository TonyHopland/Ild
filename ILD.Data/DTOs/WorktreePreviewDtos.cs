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