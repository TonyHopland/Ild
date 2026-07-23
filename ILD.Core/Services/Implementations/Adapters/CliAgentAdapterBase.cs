using System.Diagnostics;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Shared scaffolding for CLI-backed <see cref="IAgentAdapter"/> implementations
/// (claude-code, opencode, pi). These adapters all render the same prompt
/// template, persist/restore managed sessions through the same snapshot store,
/// and parse JSON event streams the same way; this base owns those identical
/// pieces so they live in exactly one place. The per-CLI parts — how the
/// process is launched and how its output is parsed/finalized — stay in the
/// concrete adapter.
/// </summary>
public abstract class CliAgentAdapterBase : IAgentAdapter
{
    private static readonly IPromptTemplateResolver Resolver = new PromptTemplateResolver();

    /// <summary>Null when constructed without DI (e.g. unit tests); session snapshot helpers no-op in that case.</summary>
    protected IServiceScopeFactory? ScopeFactory { get; }

    protected CliAgentAdapterBase()
    {
    }

    protected CliAgentAdapterBase(IServiceScopeFactory scopeFactory)
    {
        ScopeFactory = scopeFactory;
    }

    public abstract string Name { get; }
    public abstract string[] SupportedProviderTypes { get; }
    public virtual ConfigFieldDescriptor[] ConfigSchema => Array.Empty<ConfigFieldDescriptor>();

    /// <summary>
    /// The generic "Custom MCP servers (JSON)" field shared by every MCP-capable
    /// adapter (opencode, claude-code). Each such adapter surfaces it from its own
    /// <see cref="ConfigSchema"/> so the value is persisted into
    /// <c>AiProvider.Config</c> and injected alongside the built-in <c>ild</c>
    /// server. Pi is intentionally excluded — it has no MCP support by design.
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
            + "Example: {\"chrome-devtools\": {\"command\": [\"npx\", \"-y\", \"chrome-devtools-mcp@latest\", \"--headless\", \"--isolated\", \"--no-sandbox\"]}}. "
            + "Invalid JSON is ignored and never fails a run. The reserved name \"ild\" is ignored.");

    public abstract Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context);

    /// <summary>Render an AI-node prompt template against the run's placeholder context.</summary>
    protected static Task<string> RenderPromptAsync(string template, LoopRunContext context)
        => Task.FromResult(Resolver.Render(template, new PromptContext(
            WorkItemTitle: context.WorkItemTitle,
            WorkItemDescription: context.WorkItemDescription,
            PreviousNodeOutput: context.PreviousNodeOutput,
            EventLogSummary: context.EventLogSummary,
            WorktreePath: context.WorktreePath)));

    /// <summary>
    /// Start a coding-agent CLI. Every agent launch goes through here so that
    /// "a CLI launch crosses to the lower-trust agent uid" (ADR-0014) is owned by
    /// one place instead of being remembered at each call site — a missed call
    /// would silently run the agent as the orchestrator again.
    /// </summary>
    protected static Process? StartAgentProcess(ProcessStartInfo psi)
        => Process.Start(AgentUserLauncher.Route(psi));

    /// <summary>
    /// Create a directory the agent must be able to write to. Creating it and
    /// granting the agent access are one act, for the same reason
    /// <see cref="StartAgentProcess"/> exists: a later scratch directory added
    /// with a bare <c>Directory.CreateDirectory</c> would silently leave the agent
    /// unable to write it. Under uid isolation the agent runs as another uid, so
    /// an orchestrator-created directory is not writable by it by default.
    /// </summary>
    protected static void CreateAgentWritableDirectory(string path)
    {
        Directory.CreateDirectory(path);
        AgentUserLauncher.ShareScratchDirectory(path);
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
    /// Fetch the managed-session snapshot for this adapter, keyed by this adapter's
    /// <see cref="Name"/> and the execution's owner — a Chat Session when
    /// <see cref="AgentExecutionContext.ChatSessionId"/> is set, otherwise the
    /// LoopRun. Returns <c>null</c> when no DI scope is available (unit tests) or no
    /// snapshot exists. Store exceptions propagate to the caller, matching the
    /// previous per-adapter behavior.
    /// </summary>
    protected async Task<AdapterSessionSnapshot?> GetSnapshotAsync(AgentExecutionContext ctx, string sessionId, CancellationToken ct)
    {
        if (ScopeFactory is null) return null;
        await using var scope = ScopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>();
        return ctx.ChatSessionId is { } chatId
            ? await store.GetForChatAsync(chatId, Name, sessionId, ct)
            : await store.GetAsync(ctx.RunContext.LoopRunId, Name, sessionId, ct);
    }

    /// <summary>Persist a managed-session snapshot for this adapter's execution owner. No-ops without a DI scope.</summary>
    protected async Task UpsertSnapshotAsync(AgentExecutionContext ctx, string sessionId, string sessionJson, CancellationToken ct)
    {
        if (ScopeFactory is null) return;
        await using var scope = ScopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>();
        if (ctx.ChatSessionId is { } chatId)
            await store.UpsertForChatAsync(chatId, Name, sessionId, sessionJson, ct);
        else
            await store.UpsertAsync(ctx.RunContext.LoopRunId, Name, sessionId, sessionJson, ct);
    }

    /// <summary>
    /// Fork primitive shared by every CLI adapter: materialize a copy of the
    /// <paramref name="sourceSessionId"/> snapshot under <paramref name="destSessionId"/>,
    /// rewriting the transcript's embedded session ids to the destination so each
    /// CLI accepts the copy as its own resumable session. The source snapshot is
    /// only read, never written — leaving it byte-for-byte unchanged. After this
    /// call the adapter's normal restore path (keyed by the destination id)
    /// rehydrates the on-disk session and the CLI continues on the copy.
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
        var source = await store.GetAsync(loopRunId, Name, sourceSessionId, ct);
        if (source is null || string.IsNullOrWhiteSpace(source.SessionJson)) return false;

        var copy = RewriteSessionTranscript(source.SessionJson, sourceSessionId, destSessionId);
        await store.UpsertAsync(loopRunId, Name, destSessionId, copy, ct);
        return true;
    }

    /// <summary>
    /// Rewrite a session snapshot so every occurrence of the source session id
    /// becomes the destination id. Session ids are opaque, unique tokens (GUIDs /
    /// ULIDs), so a textual replace correctly retargets both the wrapping
    /// metadata and the per-event/per-message references across every adapter's
    /// snapshot format (claude wrapped JSONL, pi raw JSONL, opencode export JSON)
    /// without parsing each shape. Returns the input unchanged when either id is
    /// blank or they are equal.
    /// </summary>
    public static string RewriteSessionTranscript(string sessionJson, string sourceSessionId, string destSessionId)
    {
        if (string.IsNullOrEmpty(sessionJson)
            || string.IsNullOrEmpty(sourceSessionId)
            || string.Equals(sourceSessionId, destSessionId, StringComparison.Ordinal))
            return sessionJson;
        return sessionJson.Replace(sourceSessionId, destSessionId, StringComparison.Ordinal);
    }

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
