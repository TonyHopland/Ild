namespace ILD.Data.DTOs;

public record LoopRunContext(
    Guid LoopRunId,
    string WorkItemId,
    string WorkItemTitle,
    string WorkItemDescription,
    string WorktreePath,
    string BranchName,
    List<string> EventLogSummary,
    string? PreviousNodeOutput
);
