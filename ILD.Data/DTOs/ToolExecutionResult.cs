namespace ILD.Data.DTOs;

public record ToolExecutionResult(
    bool Success,
    string Output,
    string? Error,
    int ExitCode = 0
);
