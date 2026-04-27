namespace ILD.Core.DTOs;

public record LoopRunContext(
    Guid LoopRunId,
    Guid WorkItemId,
    string WorkItemTitle,
    string WorkItemDescription,
    string WorktreePath,
    string BranchName,
    List<string> EventLogSummary,
    string? PreviousNodeOutput
);
