using ILD.Core.Services.Implementations.Adapters;
using Porta.Pty;

namespace ILD.Core.Services.RunHosts;

/// <summary>A run host that can run a provider's sign-in terminal in a short-lived container of its own.</summary>
public interface ISignInTerminalHost
{
    /// <summary>
    /// Starts <paramref name="command"/> in a terminal in a container that no chat or run shares, with <paramref name="agent"/>
    /// (installed first if the toolchain store lacks it) on its PATH. The container is removed when the result is disposed.
    /// </summary>
    Task<SignInTerminal> OpenSignInAsync(ManagedAgent? agent, string command, IReadOnlyList<string> arguments, int cols, int rows, CancellationToken ct);
}

/// <summary>A sign-in terminal and the container it runs in; disposing it ends the terminal and removes the container.</summary>
public sealed class SignInTerminal(
    IPtyConnection pty,
    Func<string, CancellationToken, Task<string?>> readLogin,
    Func<Task> remove) : IAsyncDisposable
{
    private int _disposed;

    public IPtyConnection Pty => pty;

    /// <summary>The text of a login file in the CLI's home, by its path relative to it; null when it is not a regular file of a size worth reading.</summary>
    public Task<string?> ReadLoginAsync(string relativePath, CancellationToken ct) => readLogin(relativePath, ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { pty.Dispose(); }
        finally { await remove(); }
    }
}
