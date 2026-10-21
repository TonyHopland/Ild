using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Shared scaffolding for CLI-backed <see cref="IAgentAdapter"/> implementations
/// (claude-code, opencode, pi, codex). These adapters all persist/restore managed
/// sessions through the same snapshot store and parse JSON event streams the
/// same way; this base owns those identical pieces so they live in exactly one
/// place. The per-CLI parts — how the process is launched and how its output is
/// parsed/finalized — stay in the concrete adapter.
///
/// Adapters are transport, not templating: <see cref="AgentExecutionContext.Prompt"/>
/// reaches the CLI unchanged. Placeholder expansion belongs to the node
/// executors, which own the run context a template needs and render each
/// template field exactly once (see docs/adr/0011-context-aware-chat.md).
/// Rendering here would be a second pass over already-substituted content, run
/// against a strictly weaker context, so tokens quoted in a work item
/// description or a loop variable value would be silently eaten — and a chat
/// turn, which is not a template at all, would be holed the same way.
/// </summary>
public abstract class CliAgentAdapterBase : IAgentAdapter
{
    /// <summary>Null when constructed without DI (e.g. unit tests); session snapshot helpers no-op in that case.</summary>
    protected IServiceScopeFactory? ScopeFactory { get; }

    /// <summary>
    /// Where the adapter reads the deployment variables it launches with
    /// (<c>ILD_DATA_PATH</c>, the MCP server's URL, token and DLL, the agent read
    /// root, <c>HOME</c>): the process environment unless a test supplies its own.
    /// </summary>
    protected IProcessEnvironment EnvironmentVariables { get; }

    protected CliAgentAdapterBase(IProcessEnvironment? environment = null)
    {
        EnvironmentVariables = environment ?? ProcessEnvironment.Current;
    }

    protected CliAgentAdapterBase(IServiceScopeFactory scopeFactory, IProcessEnvironment? environment = null)
        : this(environment)
    {
        ScopeFactory = scopeFactory;
    }

    public abstract string Name { get; }
    public abstract string[] SupportedProviderTypes { get; }
    public virtual ConfigFieldDescriptor[] ConfigSchema => Array.Empty<ConfigFieldDescriptor>();

    /// <summary>
    /// Defaults to <see cref="AdapterModelSupport.Unsupported"/>: an adapter whose
    /// CLI model flag nobody has verified declares nothing and is left as it was.
    /// Overriding this is what offers the field in the UI, so the override and the
    /// flag in the arg list have to be added together — declaring support without
    /// passing the flag silently drops the model the user chose.
    /// </summary>
    public virtual AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;

    /// <summary>
    /// What makes the UI, the providers API, Extra CLI arguments validation and
    /// tool handling treat this adapter's provider type correctly: how its CLI
    /// signs in, which tools it offers, whether it can run without them, and
    /// which CLI flags ILD owns. Abstract so a new adapter cannot leave them out.
    /// </summary>
    public abstract AgentAdapterTraits Traits { get; }

    /// <summary>
    /// The generic "Custom MCP servers (JSON)" field shared by every MCP-capable
    /// adapter (opencode, claude-code, copilot, codex). Each such adapter surfaces it from
    /// its own <see cref="ConfigSchema"/> so the value is persisted into
    /// <c>AiProvider.Config</c> and injected alongside the built-in <c>ild</c>
    /// server. Pi reaches only the built-in <c>ild</c> server, registered with its
    /// own MCP client by a generated extension, so it does not expose the field.
    /// </summary>
    protected static readonly ConfigFieldDescriptor CustomMcpServersField = new(
        Name: "customMcpServersJson",
        Type: ConfigFieldType.Textarea,
        Label: "Custom MCP servers (JSON)",
        Required: false,
        DefaultValue: null,
        Description: "Optional. A JSON object mapping a server name to its definition: "
            + "{ \"name\": { \"command\": \"npx\" or [\"npx\", \"-y\", …], \"args\": [ … ], \"env\": { … } } }. "
            + "command may be a string or an array of argv tokens; args and env are optional. "
            + "These MCP servers are attached to the agent for every repository this provider runs in. "
            + "Example: {\"chrome-devtools\": {\n  \"command\": \"npx\",\n  \"args\": [\n    \"-y\", \"chrome-devtools-mcp@latest\",\n    \"--headless\", \"--isolated\",\n    \"--chrome-arg=--no-sandbox\",\n    \"--chrome-arg=--disable-setuid-sandbox\"\n  ]\n}}. "
            + "Invalid JSON is ignored and never fails a run. The reserved name \"ild\" is ignored.");

    protected static readonly ConfigFieldDescriptor ExtraArgsField = new(
        Name: "extraArgs",
        Type: ConfigFieldType.Textarea,
        Label: "Extra CLI arguments",
        Required: false,
        DefaultValue: null,
        Description: "Optional. Added to every launch of this provider's agent, e.g. --effort high. "
            + "Words are split like a shell command line: quotes group words, and nothing is expanded. "
            + "Set the model with the Model field, not here; other flags ILD already sets can't be used either. "
            + "Open the provider terminal after saving to check that the CLI accepts them. "
            + "Don't put secrets here: the arguments are visible in the process list. "
            + "Use the API key or environment settings for secrets (ADR-0014).");

    public abstract Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context);

    /// <summary>
    /// Start a coding-agent CLI. Every agent launch goes through here so that
    /// "a CLI launch crosses to the lower-trust agent uid" (ADR-0014) is owned by
    /// one place instead of being remembered at each call site — a missed call
    /// would silently run the agent as the orchestrator again.
    /// </summary>
    /// <param name="psi">The launch.</param>
    /// <param name="aiProviderId">
    /// The provider the launch is made for; the egress proxy applies that provider's
    /// scoped whitelist/blacklist entries to the child's connections.
    /// </param>
    /// <param name="environment">
    /// The execution's <see cref="AgentExecutionContext.Environment"/>, set over what
    /// <paramref name="psi"/> already carries; null for none. Required so no launch
    /// of an execution can leave it out.
    /// </param>
    protected static Process? StartAgentProcess(
        ProcessStartInfo psi, Guid aiProviderId, IReadOnlyDictionary<string, string>? environment)
    {
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            psi.Environment[name] = value;
        return Process.Start(AgentIsolation.Route(psi, aiProviderId));
    }

    /// <summary>
    /// Kill a process and its children. Never throws, but unlike a blind
    /// best-effort kill it reports whether the kill actually took effect:
    /// under uid isolation (ADR-0014) the agent runs with a different real
    /// <em>and saved</em> uid, so <c>kill(2)</c> fails with <c>EPERM</c> unless
    /// the orchestrator holds <c>CAP_KILL</c>. Silently swallowing that leaves an
    /// orphaned agent writing the worktree after a Halt or a node timeout while
    /// the engine moves on, so callers surface the failure instead.
    /// </summary>
    /// <returns><c>true</c> when the process is gone (killed, or already exited).</returns>
    protected static bool KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Already exited — that is the outcome we wanted anyway.
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Kill the agent process tree and build the node's failure message, adding a
    /// diagnostic when the kill did not take (see <see cref="KillProcessTree"/>)
    /// so an un-killable agent is visible in the run instead of silent.
    /// </summary>
    protected static string KillAndDescribe(Process process, string message)
        => KillProcessTree(process)
            ? message
            : message + " — WARNING: the agent process could not be killed and may still be running. "
                + "Under uid isolation the orchestrator needs CAP_KILL for the agent uid (see ADR-0014).";

    /// <summary>
    /// Hand the freshly-captured session id to the run's <c>OnSessionId</c>
    /// callback. Best-effort: a throwing callback must never take down the
    /// stream-read loop it runs on.
    /// </summary>
    protected static void FireSessionId(Action<string>? onSessionId, string? sessionId)
    {
        if (onSessionId is null || string.IsNullOrWhiteSpace(sessionId)) return;
        try { onSessionId(sessionId); } catch { /* best effort */ }
    }

    /// <summary>
    /// The snapshot key for this adapter's session in the execution's owner — a Chat
    /// Session when <see cref="AgentExecutionContext.ChatSessionId"/> is set, otherwise
    /// the LoopRun.
    /// </summary>
    protected SessionSnapshotKey SnapshotKey(AgentExecutionContext ctx, string sessionId)
        => ctx.ChatSessionId is { } chatId
            ? SessionSnapshotKey.ForChat(chatId, Name, sessionId)
            : SessionSnapshotKey.ForRun(ctx.RunContext.LoopRunId, Name, sessionId);

    /// <summary>
    /// Save the session file at <paramref name="path"/> after a turn. When what is stored
    /// is a prefix of the file — the file is at least as long and its bytes just before the
    /// stored length hash to the stored tail hash — only what the file gained is appended;
    /// otherwise (nothing stored yet, another format, a shorter or rewritten file) the whole
    /// file replaces it. An append that loses a race is decided again once against fresh
    /// metadata. Saving is observational: a failure is logged, and neither a failure nor a
    /// cancellation fails the turn.
    /// No-ops without a DI scope.
    /// </summary>
    protected async Task PersistSessionFileAsync(AgentExecutionContext ctx, string sessionId, string path, string format, ILogger logger)
    {
        if (ScopeFactory is null) return;
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>();
            var key = SnapshotKey(ctx, sessionId);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (await SaveSessionFileAsync(store, key, path, format, ctx.Cancel) == SessionSnapshotAppendResult.Appended) return;
            }
            logger.LogWarning("The {Adapter} session {SessionId} changed in the store while it was being saved twice running, so this turn's save was skipped", Name, sessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ctx.Cancel.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not save the {Adapter} session {SessionId}: {Reason}", Name, sessionId, ex.Message);
        }
        catch (OperationCanceledException) when (ctx.Cancel.IsCancellationRequested)
        {
            // A cancelled turn is not a failed save, and stopping the save must not fail the turn either.
            logger.LogInformation("Stopped saving the {Adapter} session {SessionId} because the turn was cancelled", Name, sessionId);
        }
    }

    private static async Task<SessionSnapshotAppendResult> SaveSessionFileAsync(
        IAdapterSessionSnapshotStore store, SessionSnapshotKey key, string path, string format, CancellationToken ct)
    {
        // The session file sits where the agent can write, so it is only measured and
        // read as the agent (see AgentWritableFiles), never through a link.
        // An empty file holds nothing to keep, and must not replace a history stored earlier.
        if (await AgentWritableFiles.GetFileLengthAsync(path, ct) is not ({ } length and > 0)) return SessionSnapshotAppendResult.Appended;

        var stored = await store.GetMetadataAsync(key, ct);
        if (stored is not null
            && stored.Format == format
            && length >= stored.ByteLength
            && await TailHashAsync(path, stored.ByteLength, ct) == stored.TailHash)
        {
            if (length == stored.ByteLength) return SessionSnapshotAppendResult.Appended;

            var tailHash = await TailHashAsync(path, length, ct);
            var result = SessionSnapshotAppendResult.Conflict;
            await AgentWritableFiles.ReadFileRangeAsync(path, stored.ByteLength, length - stored.ByteLength,
                async added => result = await store.AppendAsync(key, format, added, stored.ByteLength, tailHash, ct));
            return result;
        }

        await AgentWritableFiles.ReadFileRangeAsync(path, 0, length, whole => store.ReplaceAsync(key, format, whole, ct));
        return SessionSnapshotAppendResult.Appended;
    }

    /// <summary>The <see cref="SessionSnapshotFormats.TailHash"/> of the file's bytes just before <paramref name="end"/>.</summary>
    private static async Task<string> TailHashAsync(string path, long end, CancellationToken ct)
    {
        var length = Math.Min(end, SessionSnapshotFormats.TailWindow);
        using var tail = new MemoryStream((int)length);
        await AgentWritableFiles.ReadFileRangeAsync(path, end - length, length, content => content.CopyToAsync(tail, ct));
        return SessionSnapshotFormats.TailHash(tail.GetBuffer().AsSpan(0, (int)tail.Length));
    }

    /// <summary>
    /// Write the stored session back to <paramref name="path"/>, streamed from the store, when
    /// one of <paramref name="format"/> is stored with content. A restore that fails midway
    /// leaves no file there. Returns whether it wrote the file; false without a DI scope.
    /// </summary>
    protected async Task<bool> RestoreSessionFileAsync(AgentExecutionContext ctx, string sessionId, string path, string format)
    {
        if (ScopeFactory is null) return false;
        await using var scope = ScopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>();
        var key = SnapshotKey(ctx, sessionId);
        var stored = await store.GetMetadataAsync(key, ctx.Cancel);
        if (stored is null || stored.Format != format || stored.ByteLength == 0) return false;

        await using var content = await store.OpenReadAsync(key, 0, ctx.Cancel);
        if (content is null) return false;
        await AgentWritableFiles.CreateDirectoryAsync(Path.GetDirectoryName(path)!, ctx.Cancel);
        await AgentWritableFiles.WriteFileAsync(path, content, ctx.Cancel);
        return true;
    }

    /// <summary>
    /// What <paramref name="parse"/> makes of the session file at <paramref name="path"/>,
    /// read line by line as the agent without holding the file whole; the default when
    /// there is no regular file there.
    /// </summary>
    protected static async Task<T?> ParseSessionFileAsync<T>(string path, Func<TextReader, T> parse, CancellationToken ct)
    {
        if (await AgentWritableFiles.GetFileLengthAsync(path, ct) is not { } length) return default;
        T? result = default;
        await AgentWritableFiles.ReadFileRangeAsync(path, 0, length, content =>
        {
            using var reader = new StreamReader(content, Encoding.UTF8);
            result = parse(reader);
            return Task.CompletedTask;
        });
        return result;
    }

    /// <summary>
    /// Fork primitive shared by every CLI adapter: materialize a copy of the
    /// <paramref name="sourceSessionId"/> snapshot under <paramref name="destSessionId"/>,
    /// with the transcript's embedded session ids rewritten to the destination so each
    /// CLI accepts the copy as its own resumable session. The store streams the copy a
    /// chunk at a time; the source is only read, never written. After this call the
    /// adapter's normal restore path (keyed by the destination id) rehydrates the
    /// on-disk session and the CLI continues on the copy.
    /// </summary>
    /// <returns>
    /// <c>true</c> when a copy was materialized; <c>false</c> when there is no DI
    /// scope or the source has no snapshot — in which case the caller starts a
    /// fresh session, matching the "source has no bound session" behavior.
    /// </returns>
    protected async Task<bool> ForkSessionSnapshotAsync(Guid loopRunId, string sourceSessionId, string destSessionId, CancellationToken ct)
    {
        if (ScopeFactory is null) return false;
        await using var scope = ScopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>();
        return await store.ForkAsync(
            SessionSnapshotKey.ForRun(loopRunId, Name, sourceSessionId),
            SessionSnapshotKey.ForRun(loopRunId, Name, destSessionId),
            ct);
    }

    /// <summary>
    /// Remove the NUL (U+0000) character from captured CLI output. It is the one
    /// character a PostgreSQL <c>text</c>/<c>varchar</c> column cannot store, so
    /// it must never reach the node output/error the engine persists. Returns the
    /// input unchanged when it holds no NUL (the common case).
    /// </summary>
    public static string StripNullChars(string value)
        => value.IndexOf('\0') < 0 ? value : value.Replace("\0", string.Empty);

    /// <summary>Try to read a string property from a JSON object element.</summary>
    protected static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Read a string property from a JSON object element, or <c>null</c> if absent/non-string.</summary>
    protected static string? GetString(JsonElement element, string propertyName)
        => TryGetString(element, propertyName, out var value) ? value : null;
}
