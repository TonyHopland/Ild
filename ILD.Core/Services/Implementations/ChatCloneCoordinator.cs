namespace ILD.Core.Services.Implementations;

/// <summary>
/// Per chat, the clone work <see cref="ChatRepositoryCloner"/> does for it: one
/// clone or update at a time, so two calls never pick the same folder or
/// interleave their git commands, and a stop the chat's deletion fires, so no
/// clone outlives the chat. A chat's entry lives only while a clone or a
/// deletion uses it. A singleton: the work it guards outlives the request scope
/// that started it.
/// </summary>
public sealed class ChatCloneCoordinator
{
    private readonly SharedEntries<Guid, Chat> _chats = new(() => new Chat());

    private sealed class Chat : IDisposable
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource Deleted { get; } = new();

        public void Dispose()
        {
            Deleted.Dispose();
            Gate.Dispose();
        }
    }

    /// <summary>
    /// Run <paramref name="work"/> alone for the chat, on a token cancelled by
    /// <paramref name="shutdown"/> or by the chat's deletion. Null when the chat
    /// was deleted before or while it ran; a shutdown is rethrown.
    /// </summary>
    public async Task<T?> RunExclusiveAsync<T>(
        Guid chatSessionId, CancellationToken shutdown, Func<CancellationToken, Task<T>> work)
        where T : class
    {
        var chat = _chats.Enter(chatSessionId);
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(shutdown, chat.Deleted.Token);
            try
            {
                await chat.Gate.WaitAsync(stop.Token);
            }
            catch (OperationCanceledException) when (chat.Deleted.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                return await work(stop.Token);
            }
            catch (OperationCanceledException) when (chat.Deleted.IsCancellationRequested)
            {
                return null;
            }
            finally
            {
                chat.Gate.Release();
            }
        }
        finally
        {
            _chats.Leave(chatSessionId);
        }
    }

    /// <summary>
    /// Stop the deleted chat's clone work — git it runs is killed and calls
    /// waiting for it are refused — and wait until none runs. Call after the
    /// chat's row is gone and before its scratch folder is removed: work that
    /// starts later finds the row gone once it runs.
    /// </summary>
    public async Task StopForDeletedChatAsync(Guid chatSessionId)
    {
        var chat = _chats.Enter(chatSessionId);
        try
        {
            await chat.Deleted.CancelAsync();
            await chat.Gate.WaitAsync();
            chat.Gate.Release();
        }
        finally
        {
            _chats.Leave(chatSessionId);
        }
    }

    /// <summary>Whether a clone or a deletion of the chat holds its entry.</summary>
    internal bool IsTracked(Guid chatSessionId) => _chats.Contains(chatSessionId);
}
