namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Clones a repository into a scheduled chat's scratch folder, or updates the
/// clone it made there before, for the chat's <c>clone_repository</c> tool.
/// ILD fetches its own base clone of the repository with the remote provider's
/// credentials; the chat's clone is filled from that base clone by git running
/// as the agent, so no credential ever reaches the chat's folder.
/// </summary>
public interface IChatRepositoryCloner
{
    /// <summary>
    /// Clone or update <paramref name="repositoryId"/> on <paramref name="branch"/>
    /// (the repository's default branch when null) in the chat's
    /// <c>repos/</c> folder. The git work runs to completion even when
    /// <paramref name="cancellationToken"/> is cancelled: only the wait for it ends.
    /// </summary>
    Task<ChatRepositoryCloneResult> CloneAsync(
        Guid chatSessionId, string repositoryId, string? branch, CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IChatRepositoryCloner.CloneAsync"/> did, or why it refused.</summary>
public enum ChatRepositoryCloneOutcome
{
    /// <summary>A new clone was made in a folder of its own.</summary>
    Cloned,

    /// <summary>The chat's existing clone was fast-forwarded to, or switched to, the branch's remote head.</summary>
    Updated,

    /// <summary>The chat-session header names no chat.</summary>
    UnknownChat,

    /// <summary>The chat was not started by a schedule (or its schedule was deleted), so it has no allowlist.</summary>
    NotScheduledChat,

    /// <summary>The schedule's repository allowlist does not include the repository.</summary>
    NotAllowed,

    /// <summary>The repository id is no GUID, or names no repository ILD knows.</summary>
    UnknownRepository,

    /// <summary>The branch name is not a valid git branch name.</summary>
    InvalidBranch,

    /// <summary>The remote has no such branch.</summary>
    BranchNotFound,

    /// <summary>ILD could not clone or fetch its own copy of the repository.</summary>
    FetchFailed,

    /// <summary>
    /// A step in the chat's own clone failed for a reason other than local work
    /// in the way: its folder could not be made, or git failed there. The message
    /// carries git's text.
    /// </summary>
    CloneFailed,

    /// <summary>
    /// Git refused the update or the branch switch because it would lose local
    /// work (uncommitted changes in the way, or local commits that make the
    /// branch diverge). Nothing was changed; the message carries git's own text.
    /// </summary>
    UpdateRefused,
}

/// <summary>
/// The result of a clone or update. <paramref name="Path"/> is relative to the
/// chat's working directory (its scratch folder); <paramref name="PreviousCommit"/>
/// is null for a fresh clone. Only <see cref="ChatRepositoryCloneOutcome.Cloned"/>
/// and <see cref="ChatRepositoryCloneOutcome.Updated"/> carry the clone's fields.
/// </summary>
public sealed record ChatRepositoryCloneResult(
    ChatRepositoryCloneOutcome Outcome,
    string Message,
    string? Path = null,
    string? Branch = null,
    string? Commit = null,
    string? PreviousCommit = null,
    bool Changed = false)
{
    public bool Success => Outcome is ChatRepositoryCloneOutcome.Cloned or ChatRepositoryCloneOutcome.Updated;
}
