using System.Net.WebSockets;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Data.Entities;
using Porta.Pty;

namespace ILD.Api.Services;

/// <summary>
/// Runs an AI provider's interactive TUI in a PTY rooted at a fresh scratch
/// directory. Hands the WebSocket↔PTY transport off to
/// <see cref="PtyWebSocketBridge"/>; this class just resolves the binary
/// from the provider config and manages the scratch dir lifecycle.
/// </summary>
public sealed class InteractiveProviderSessionService
{
    private readonly ILogger<InteractiveProviderSessionService> _logger;

    public InteractiveProviderSessionService(ILogger<InteractiveProviderSessionService> logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(
        WebSocket socket,
        AiProvider provider,
        int initialCols,
        int initialRows,
        CancellationToken cancellationToken)
    {
        var binaryPath = ResolveBinaryPath(provider);
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            await PtyWebSocketBridge.SendErrorAndCloseAsync(
                socket, "Provider has no binaryPath configured.", cancellationToken);
            return;
        }

        // Coding agents are no longer baked into the image. If the resolved
        // command doesn't exist (no /data install yet, not on PATH), spawning
        // would fail with an opaque error — surface an actionable one instead.
        if (!CommandResolves(binaryPath))
        {
            await PtyWebSocketBridge.SendErrorAndCloseAsync(
                socket, BuildUnavailableMessage(provider, binaryPath), cancellationToken);
            return;
        }

        var sessionId = Guid.NewGuid();
        // The TUI runs as the agent uid (below), so its cwd has to be writable by
        // it. Rooting it at the shared scratch root means the setgid shared-group
        // tree grants that by inheritance, rather than a per-directory chmod.
        var sessionRoot = AgentIsolation.CreateScratchDirectory("ild-sessions", sessionId.ToString("N"));

        try
        {
            // Run the login TUI as the *agent* uid, exactly like a run (ADR-0014).
            // These CLIs write their credentials with owner-only modes
            // (~/.claude/.credentials.json is 0600, opencode's auth.json likewise),
            // which no group or ACL grant can widen after the fact — so the login
            // has to be performed by the uid that later has to read them, or the
            // agent reads as logged-out until the next boot repair. It also means
            // this is no longer a second, unrouted agent-CLI launch path.
            var routed = AgentIsolation.RouteCommand(binaryPath, Array.Empty<string>());
            var environment = new Dictionary<string, string>();
            if (AgentIsolation.AgentHome is { } agentHome)
                environment["HOME"] = agentHome;

            var options = new PtyOptions
            {
                Name = $"ild-{provider.Type}",
                Cols = Math.Clamp(initialCols, 20, 500),
                Rows = Math.Clamp(initialRows, 5, 200),
                Cwd = sessionRoot,
                App = routed.FileName,
                CommandLine = routed.Arguments.ToArray(),
                Environment = environment,
            };

            await PtyWebSocketBridge.RunAsync(socket, options, _logger, cancellationToken);
        }
        finally
        {
            try { Directory.Delete(sessionRoot, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Maps an AiProvider <c>Type</c> to the binary name shipped by its
    /// vendor's CLI. For most adapters the type matches the binary name
    /// (<c>opencode</c>, <c>pi</c>), but <c>claude-code</c> ships as
    /// <c>claude</c>.
    /// </summary>
    private static string DefaultBinaryForType(string? providerType)
    {
        if (string.IsNullOrWhiteSpace(providerType))
            return string.Empty;

        return providerType.ToLowerInvariant() switch
        {
            "claude-code" => "claude",
            var other => other,
        };
    }

    /// <summary>
    /// Resolve the CLI to launch for the interactive terminal. An explicit
    /// <c>binaryPath</c> in the provider config wins; otherwise a managed agent
    /// (pi, opencode, claude-code) prefers its <c>/data</c> install — falling
    /// back to the bare command on PATH — exactly as the loop adapters resolve
    /// it via <see cref="ManagedAgentInstall.ResolveCommand(ManagedAgent, string)"/>.
    /// Without this the terminal would ignore the install the user just made on
    /// the AI Provider page and try a binary that is no longer baked into the image.
    /// </summary>
    public static string ResolveLaunchCommand(AiProvider provider, string dataRoot)
    {
        var config = AiProviderConfig.Parse(provider.Config);
        var fallback = ManagedAgentCatalog.Find(provider.Type) is { } agent
            ? ManagedAgentInstall.ResolveCommand(agent, dataRoot)
            : DefaultBinaryForType(provider.Type);
        return config.BinaryPathOr(fallback);
    }

    private static string ResolveBinaryPath(AiProvider provider)
        => ResolveLaunchCommand(provider, ManagedAgentInstall.ResolveDataRoot());

    /// <summary>
    /// Actionable message shown in the terminal when the resolved CLI can't be
    /// launched: for a managed agent point the user at this page's Install
    /// button; otherwise flag the misconfigured <c>binaryPath</c>.
    /// </summary>
    public static string BuildUnavailableMessage(AiProvider provider, string binaryPath)
        => ManagedAgentCatalog.Find(provider.Type) is { } managed
            ? $"{managed.DisplayName} isn't installed. Use this provider's \"Install\" button on the AI Provider page, then reopen the terminal."
            : $"'{binaryPath}' was not found. Configure a valid binaryPath for this provider.";

    /// <summary>
    /// True when <paramref name="command"/> can actually be launched: an explicit
    /// path must exist, a bare command must be found on <c>PATH</c>.
    /// </summary>
    private static bool CommandResolves(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(command);

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
            return false;

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, command)))
                return true;
        }
        return false;
    }
}
