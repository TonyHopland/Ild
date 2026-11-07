using System.Net.WebSockets;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Local;
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
    private readonly IEgressPolicy? _egressPolicy;
    private readonly IManagedAgentUsage? _usage;
    private readonly IServiceScopeFactory? _scopes;
    private readonly IRunHost _host;

    public InteractiveProviderSessionService(
        ILogger<InteractiveProviderSessionService> logger,
        IEgressPolicy? egressPolicy = null,
        IManagedAgentUsage? usage = null,
        IServiceScopeFactory? scopes = null,
        IRunHost? host = null)
    {
        _host = host ?? LocalRunHost.Shared;
        _logger = logger;
        _egressPolicy = egressPolicy;
        _usage = usage;
        _scopes = scopes;
    }

    public async Task RunAsync(
        WebSocket socket,
        AiProvider provider,
        int initialCols,
        int initialRows,
        CancellationToken cancellationToken)
    {
        // Taken before the launch command is resolved, so an update cannot swap the
        // install between resolving the path and starting the process.
        using var lease = _usage?.Acquire(provider.Type);
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
            // The routed command carries the environment the crossing requires
            // (HOME, so the TUI writes credentials into the agent's home rather
            // than the orchestrator's); applying it is not optional.
            // Only the user's extra arguments: the terminal is for logging in and for
            // checking that the CLI accepts them, not a run, so none of a run's flags.
            var (extraArgs, notice) = ResolveExtraArgs(provider);
            // The CLI runs in a home of its own, thrown away afterwards: what it writes there is
            // the agent's to write, so the logins it makes are imported into the credential store
            // by ILD when the terminal closes, rather than written into the store by the agent
            // (ADR-0031). A Claude terminal makes the long-lived token that the gateway uses.
            var throwawayHome = Path.Combine(sessionRoot, "home");
            await _host.CreateDirectoryAsync(throwawayHome, cancellationToken);
            var claudeToken = provider.Type == "claude-code" ? new SetupTokenCapture(Math.Clamp(initialCols, 20, 500)) : null;
            if (claudeToken is not null)
                extraArgs = ["setup-token"];
            if (notice is not null)
                await PtyWebSocketBridge.SendNoticeAsync(socket, notice, cancellationToken);
            var routed = _host.RouteTerminalCommand(
                binaryPath, extraArgs, provider.Id, await _egressPolicy.LaunchModeAsync(cancellationToken));
            var environment = new Dictionary<string, string>(routed.Environment) { ["HOME"] = throwawayHome };

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

            await PtyWebSocketBridge.RunAsync(
                socket, options, _logger, cancellationToken, reportExit: true,
                onOutput: claudeToken is null ? null : claudeToken.Feed, host: _host);
            await KeepLoginsAsync(provider, throwawayHome, claudeToken?.Token);
        }
        finally
        {
            // The TUI ran as the agent, which can write here: cleared as the agent,
            // so a link it planted is unlinked rather than followed.
            try { await _host.DeleteAsync([sessionRoot], CancellationToken.None); } catch { }
        }
    }

    /// <summary>
    /// What the terminal made, kept where only ILD can read it: the logins it wrote go into the credential
    /// store, and a long-lived Claude token it printed is saved, encrypted, on the provider.
    /// </summary>
    private async Task KeepLoginsAsync(AiProvider provider, string throwawayHome, string? claudeToken)
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
                await CredentialImport.ImportAsync(throwawayHome, home, _logger, CancellationToken.None);

            if (claudeToken is not null && _scopes is not null)
            {
                await using var scope = _scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IProviderStore>();
                if (await store.GetAiProviderByIdAsync(provider.Id) is { } current)
                {
                    current.ApiKey = claudeToken;
                    await store.UpdateAiProviderAsync(current);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not keep what the {ProviderName} terminal produced: {Reason}", provider.Name, ex.Message);
        }
    }

    /// <summary>
    /// The extra CLI arguments saved on <paramref name="provider"/>, and the line to
    /// show above the terminal about them: what it was launched with, or that a
    /// value which does not split was ignored. No arguments, no line.
    /// </summary>
    private (IReadOnlyList<string> Tokens, string? Notice) ResolveExtraArgs(AiProvider provider)
    {
        var (tokens, error) = ExtraCliArgs.Tokenize(AiProviderConfig.Parse(provider.Config).ExtraArgs);
        if (error is not null)
        {
            _logger.LogWarning(
                "Opening the terminal of AI provider {ProviderName} ({ProviderId}) without its extra CLI arguments: {Error}",
                provider.Name, provider.Id, error);
            return (tokens, $"Extra CLI arguments ignored: {error} Fix them on the AI Providers page.");
        }
        return (tokens, tokens.Count == 0 ? null : "Launched with: " + ExtraCliArgs.Format(tokens));
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
