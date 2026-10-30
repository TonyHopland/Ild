namespace ILD.Core.Services.Implementations;

/// <summary>
/// A worktree file exists but the orchestrator is denied reading it, even after
/// its group access was repaired. The message is for the person looking at the file.
/// </summary>
public sealed class WorktreeFileUnreadableException : IOException
{
    public WorktreeFileUnreadableException(string message) : base(message)
    {
    }
}
