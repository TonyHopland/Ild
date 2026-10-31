namespace ILD.Core.Services.Implementations;

/// <summary>
/// A worktree file exists but the orchestrator is denied reading it, even after
/// its group access was repaired. The message names the file and is for logs;
/// callers answer the person looking at it with their own text.
/// </summary>
public sealed class WorktreeFileUnreadableException : IOException
{
    public WorktreeFileUnreadableException(string message) : base(message)
    {
    }
}
