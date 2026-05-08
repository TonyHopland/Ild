namespace ILD.Core.Services.Remote;

/// <summary>
/// Configuration for the remote WorkItem server connection. Sourced from a
/// repository row in ILD's local SQLite (per-repository, since the PRD calls
/// for a 1:1 mapping between repository and remote provider).
/// </summary>
public sealed class WorkItemServerOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}
