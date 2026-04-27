namespace ILD.Core.DTOs;

public class LoopRunDto
{
    public string Id { get; set; } = string.Empty;
    public string WorkItemId { get; set; } = string.Empty;
    public string LoopTemplateId { get; set; } = string.Empty;
    public int TemplateVersion { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? CurrentNodeId { get; set; }
    public bool IsPaused { get; set; }
    public int NodeExecutionCount { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<LoopRunNodeDto> Nodes { get; set; } = new();
}

public class LoopRunNodeDto
{
    public string Id { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string NodeLabel { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Output { get; set; }
    public string? Error { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int ExecutionCount { get; set; }
}
