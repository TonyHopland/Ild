using System.Net.WebSockets;
using System.Runtime.InteropServices;
using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Local;
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
    private readonly IRunHost _host;

    public InteractiveShellSessionService(ILogger<InteractiveShellSessionService> logger, IRunHost host)
    {
        _logger = logger;
        _host = host;
    }

    /// <param name="sessionLabel">
    /// Names the PTY and prefixes the session's package feed credential file, so a
    /// run id here lets reclaiming the run find it.
    /// </param>
    /// <param name="packageFeeds">
    /// The selected package feeds of the repository the worktree belongs to. The
    /// shell gets their npm user config and NuGet endpoints, exactly as the run's
    /// own processes do; the file lives as long as the session.
    /// </param>
    public async Task RunAsync(
        WebSocket socket,
        string cwd,
        string sessionLabel,
        int initialCols,
        int initialRows,
        IReadOnlyList<PackageFeedCredential> packageFeeds,
        CancellationToken cancellationToken)
    {
        if (!await _host.DirectoryExistsAsync(cwd, cancellationToken))
        {
            await PtyWebSocketBridge.SendErrorAndCloseAsync(
                socket, $"Working directory does not exist: {cwd}", cancellationToken);
            return;
        }

        using var feeds = PackageFeedCredentialFiles.Materialize(packageFeeds, sessionLabel, _logger);
        var options = new PtyOptions
        {
            Name = $"ild-shell-{sessionLabel}",
            Cols = Math.Clamp(initialCols, 20, 500),
            Rows = Math.Clamp(initialRows, 5, 200),
            Cwd = cwd,
            App = ResolveShell(),
            CommandLine = Array.Empty<string>(),
            Environment = new Dictionary<string, string>(feeds.Environment),
        };

        await PtyWebSocketBridge.RunAsync(socket, options, _logger, cancellationToken, host: _host);
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
