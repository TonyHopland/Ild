using System.Net.WebSockets;
using System.Runtime.InteropServices;
using Porta.Pty;

namespace ILD.Api.Services;

/// <summary>
/// Runs a system shell in a PTY rooted at a caller-supplied working directory
/// (e.g. a run's git worktree while it is parked at human feedback). Hands
/// the WebSocket↔PTY transport off to <see cref="PtyWebSocketBridge"/>;
/// this class just resolves the shell binary and verifies the cwd.
///
/// Unlike <see cref="InteractiveProviderSessionService"/>, the cwd is owned
/// by the caller and is NOT deleted on disconnect.
/// </summary>
public sealed class InteractiveShellSessionService
{
    private readonly ILogger<InteractiveShellSessionService> _logger;

    public InteractiveShellSessionService(ILogger<InteractiveShellSessionService> logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(
        WebSocket socket,
        string cwd,
        string sessionLabel,
        int initialCols,
        int initialRows,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(cwd))
        {
            await PtyWebSocketBridge.SendErrorAndCloseAsync(
                socket, $"Working directory does not exist: {cwd}", cancellationToken);
            return;
        }

        var options = new PtyOptions
        {
            Name = $"ild-shell-{sessionLabel}",
            Cols = Math.Clamp(initialCols, 20, 500),
            Rows = Math.Clamp(initialRows, 5, 200),
            Cwd = cwd,
            App = ResolveShell(),
            CommandLine = Array.Empty<string>(),
            Environment = new Dictionary<string, string>(),
        };

        await PtyWebSocketBridge.RunAsync(socket, options, _logger, cancellationToken);
    }

    private static string ResolveShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Environment.GetEnvironmentVariable("COMSPEC") ?? "powershell.exe";
        }
        var fromEnv = Environment.GetEnvironmentVariable("SHELL");
        return string.IsNullOrWhiteSpace(fromEnv) ? "/bin/bash" : fromEnv;
    }
}
