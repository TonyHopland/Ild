namespace ILD.Core.Services.Interfaces;

/// <summary>Titles chats in the background, so a turn never waits on a title model.</summary>
public interface IChatTitleScheduler
{
    /// <summary>Starts titling the chat; the returned job never throws. A chat already being titled gets the running job.</summary>
    Task Schedule(Guid chatSessionId, string? openWorkItemId);
}
