namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Hands a chat's title off to the background once its first exchange is stored,
/// so the turn never waits on a title model.
/// </summary>
public interface IChatTitleScheduler
{
    /// <summary>
    /// Start titling the chat, with the work item that was open when its first
    /// message was sent as context. Returns the job, which never throws: a title
    /// that cannot be made is logged and the chat keeps the one it has. A chat
    /// already being titled gets the job that is running.
    /// </summary>
    Task Schedule(Guid chatSessionId, string? openWorkItemId);
}
