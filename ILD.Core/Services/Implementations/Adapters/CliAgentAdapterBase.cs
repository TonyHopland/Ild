using ILD.Core.Services.Implementations.Files;
using ILD.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Local;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Shared scaffolding for CLI-backed <see cref="IAgentAdapter"/> implementations
/// (claude-code, opencode, pi, codex). These adapters all persist/restore managed
/// sessions through the same snapshot store and parse JSON event streams the
/// same way; this base owns those identical pieces so they live in exactly one
/// place, the turn itself among them (<see cref="ExecuteAsync"/>). The per-CLI parts — how the process is launched and how
/// its output is parsed/finalized — are the adapter's <see cref="CliAgentTurn"/>.
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

    /// <summary>The environment as the launch of <paramref name="ctx"/> sees ILD: for the config files that point the agent's MCP server at ILD.</summary>
    protected IProcessEnvironment RunFacingEnvironmentFor(AgentExecutionContext ctx)
        => new RunFacingEnvironment(EnvironmentVariables, RunHost, ctx.RunContext.WorktreePath, ctx.AgentApiToken);

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
            + "Use the API key or environment settings for secrets.");

    /// <summary>The CLI's name as failures carry it: <c>[name-error]</c>, <c>name timed out</c>.</summary>
    protected abstract string CliName { get; }

    /// <summary>What to do about a CLI that cannot be started, as a sentence that follows "cannot start 'x' — ".</summary>
    protected abstract string StartHelp { get; }

    protected virtual ILogger Logger => NullLogger.Instance;

    /// <summary>
    /// The turn of <paramref name="ctx"/>, with nothing done yet: it is disposed however the turn ends. The lease on the model gateway
    /// is opened when the turn first reads <paramref name="gateway"/>, so what it does before that (a fork, a restore) holds none.
    /// </summary>
    protected abstract CliAgentTurn CreateTurn(AgentExecutionContext ctx, string worktreePath, Lazy<ModelGatewayLease?> gateway);

    public virtual async Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
    {
        // Launching anyway would hand a caller that asked for no tools every tool the CLI has.
        if (ctx.NoTools && !Traits.SupportsNoTools)
            return NodeExecutionResult.Fail(
                $"[{CliName}-error] the {Traits.DisplayName} CLI cannot run without its tools, so it cannot take a call that must have none.");

        CliAgentTurn? turn = null;
        var gateway = new Lazy<ModelGatewayLease?>(() => OpenGateway(ctx));
        try
        {
            var worktreePath = ctx.RunContext.WorktreePath;
            if (string.IsNullOrWhiteSpace(worktreePath) || !await RunHost.DirectoryExistsAsync(worktreePath, ctx.Cancel))
                return NodeExecutionResult.Fail(
                    $"[{CliName}-error] AI node requires a valid worktree path; refusing to run outside the loop's worktree.");

            turn = CreateTurn(ctx, worktreePath, gateway);
            return await RunAsync(turn, ctx);
        }
        catch (AgentTurnFailedException failed)
        {
            return failed.Result;
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Fail($"[{CliName}-error] {ex.Message}");
        }
        finally
        {
            if (gateway.IsValueCreated) gateway.Value?.Dispose();
            if (turn is not null) await turn.DisposeAsync();
        }
    }

    private async Task<NodeExecutionResult> RunAsync(CliAgentTurn turn, AgentExecutionContext ctx)
    {
        await turn.PrepareAsync();
        await PrepareSessionAsync(turn, ctx);

        IRunProcess? started;
        try
        {
            started = await StartAsync(await turn.LaunchAsync(), ctx);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            if (await turn.RetryLaunchAsync(ex) is not { } retry)
                return NodeExecutionResult.Fail(CannotStart(turn, ex));
            try
            {
                started = await StartAsync(retry, ctx);
            }
            catch (Exception retryEx) when (retryEx is InvalidOperationException or IOException)
            {
                return NodeExecutionResult.Fail(CannotStart(turn, retryEx));
            }
        }

        using var process = started ?? throw new InvalidOperationException("Process.Start returned null");
        await turn.SendInputAsync(process);

        var stdoutTask = turn.ReadStdoutAsync(process.StandardOutput);
        var stderrTask = turn.ReadStderrAsync(process.StandardError, ctx.Cancel);

        try
        {
            await process.WaitForExitAsync(ctx.Cancel);
        }
        catch (OperationCanceledException)
        {
            var timedOut = KillAndDescribe(process, $"{CliName} timed out");
            await turn.AfterTimeoutAsync(stdoutTask);
            return NodeExecutionResult.Fail(timedOut);
        }

        string stderr;
        try
        {
            await stdoutTask;
            stderr = await stderrTask;
        }
        catch (OperationCanceledException)
        {
            return NodeExecutionResult.Fail(KillAndDescribe(process, $"{CliName} stream read timed out"));
        }

        var result = await CompleteAsync(turn, ctx, stderr, process.ExitCode);
        if (!result.Success && process.Termination is { Kind: not RunTerminationKind.Other } ended)
            return result with { Error = ended.Describe(), Failure = FailureKind.Failed, Termination = ended };
        return result;
    }

    private Task<IRunProcess> StartAsync(AgentLaunch launch, AgentExecutionContext ctx)
        => StartAgentProcessAsync(launch.StartInfo, ctx, launch.Environment, launch.AgentFiles);

    private string CannotStart(CliAgentTurn turn, Exception ex)
        => $"[{CliName}-error] cannot start '{turn.BinaryPath}' — {StartHelp} Details: {ex.Message}";

    /// <summary>Whether ILD keeps the execution's session (<see cref="AgentExecutionContext.ManageSession"/>) on a CLI that resumes sessions at all.</summary>
    private bool ManagesSession(AgentExecutionContext ctx) => ctx.ManageSession && Traits.SupportsSessionResume;

    /// <summary>
    /// The session protocol up to the launch, in the order it has to run in: fork the session the execution continues from, restore
    /// the stored session where the CLI reads it, then read what the turn needs of it. A fork that fails fails the turn; a source
    /// with no snapshot leaves nothing to resume, so a new session starts.
    /// </summary>
    private async Task PrepareSessionAsync(CliAgentTurn turn, AgentExecutionContext ctx)
    {
        var resume = Traits.SupportsSessionResume && !string.IsNullOrWhiteSpace(ctx.SessionId) ? ctx.SessionId : null;
        if (ManagesSession(ctx) && resume is { } managed && turn.IsSessionId(managed))
        {
            await ForkManagedSessionAsync(ctx);
            if (!await turn.RestoreSessionAsync(managed))
                resume = null;
        }
        turn.ResumeSessionId = await turn.ReadSessionAsync(resume);
    }

    /// <summary>Seeds a copy of the session the execution forks from under the session it runs on, so restoring that one brings up the copy and the CLI continues on the fork while the source stays as it was.</summary>
    private async Task ForkManagedSessionAsync(AgentExecutionContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.ForkFromSessionId)) return;
        try
        {
            await ForkSessionSnapshotAsync(ctx.RunContext.LoopRunId, ctx.ForkFromSessionId, ctx.SessionId!, ctx.Cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AgentTurnFailedException(NodeExecutionResult.Fail(
                $"[{CliName}-error] could not fork the {CliName} session {ctx.ForkFromSessionId} into {ctx.SessionId}: {ex.Message}"));
        }
    }

    /// <summary>
    /// The turn's result once the CLI has exited and its output is read: the session is saved first (whatever the turn is then
    /// classified as, as long as the CLI exited cleanly: there is a transcript to resume from either way), then the outcome is decided.
    /// </summary>
    private async Task<NodeExecutionResult> CompleteAsync(CliAgentTurn turn, AgentExecutionContext ctx, string stderr, int exitCode)
    {
        var stream = turn.ParseStream();
        var sessionId = stream.SessionId ?? turn.ResumeSessionId;

        if (exitCode == 0 && ManagesSession(ctx) && !string.IsNullOrWhiteSpace(sessionId)
            && await turn.SaveSessionAsync(sessionId) is { } saved && string.IsNullOrWhiteSpace(stream.Text))
            stream = stream with { Text = saved };

        var result = Classify(
            stream, stderr, exitCode, CliName, ctx.Prompt, sessionId, Traits.SupportsSessionResume ? ctx.IncomingSessionId : null, turn.BlankOutput,
            turn.ProviderErrorIsTheFailure, turn.RawEventsExplainFailure);
        return result.Success ? result with { Usage = await turn.UsageAsync() } : result;
    }

    /// <summary>
    /// What a finished turn is: the same for every CLI, from its parsed output, stderr and exit code.
    /// <list type="bullet">
    /// <item>No answer stands in for a diagnostic (the provider's error, else stderr, else a plain "no text"), except that the raw
    /// output is the answer when it was not an event stream at all; what a blank one is, is the CLI's <see cref="BlankOutput"/>.</item>
    /// <item>A clean exit is still a failure when the provider reported an error, when the stream ended before its turn did, or when
    /// there was no answer. A non-zero exit is always one, with the provider's error as its reason when it gave one; with
    /// <paramref name="providerErrorIsTheFailure"/> that error is the whole of the failure at any exit code, and stderr no part of it.
    /// With nothing else to go on its error ends with the answer, else with the "no text" note, or with the tail of the raw events
    /// for a CLI whose <paramref name="rawEventsExplainFailure"/> says they explain it.</item>
    /// <item>NUL is stripped from everything: it is the one character a PostgreSQL text column cannot store.</item>
    /// </list>
    /// </summary>
    internal static NodeExecutionResult Classify(
        StreamResult stream, string stderr, int exitCode, string cliName, string prompt, string? sessionId, string? incomingSessionId,
        BlankOutput blankOutput = BlankOutput.Answer, bool providerErrorIsTheFailure = false, bool rawEventsExplainFailure = false)
    {
        var text = StripNullChars(stream.Text);
        var raw = StripNullChars(stream.RawStdout);
        stderr = StripNullChars(stderr);
        var providerError = string.IsNullOrWhiteSpace(stream.ProviderError) ? null : StripNullChars(stream.ProviderError);

        var hasText = !string.IsNullOrWhiteSpace(text);
        var blank = !hasText && !stream.SawJsonEvents && string.IsNullOrWhiteSpace(raw);
        var rawIsAnswer = !hasText && !stream.SawJsonEvents && (!blank || blankOutput == BlankOutput.Answer);
        var response = hasText ? text
            : rawIsAnswer ? raw.Trim()
            : providerError is not null ? $"[{cliName}-error] {providerError}"
            : !string.IsNullOrWhiteSpace(stderr) ? $"[{cliName}] no assistant text response. stderr: {stderr.Trim()}"
            : $"[{cliName}] no assistant text response from model";

        // The provider's own message, apart from the agent's narration, is what tells a rate limit from a real failure.
        var failure = providerError is null ? FailureKind.Unknown : AiFailureClassifier.ClassifyProviderMessage(providerError);

        if (providerError is not null && providerErrorIsTheFailure)
            return NodeExecutionResult.Fail($"[{cliName}-error] {providerError}", hasText ? text : null, failure);
        if (exitCode != 0)
            return NodeExecutionResult.Fail(
                AdapterFailureText.NonZeroExit(
                    exitCode, stderr, providerError, hasText ? text : stream.SawJsonEvents && !rawEventsExplainFailure ? response : raw),
                response, failure);
        if (providerError is not null)
            return NodeExecutionResult.Fail($"{cliName} session error: {providerError}", response, failure);
        // The CLI can exit cleanly mid-turn (the provider closed the stream), leaving half a sentence that must not pass for the answer.
        if (stream.Truncation is { } truncated)
            return NodeExecutionResult.Fail(truncated, response);
        // A turn that completed with no text (the provider dropped the request) is a failure the on_failure edge can recover from,
        // not a diagnostic to pass on as the AI's output.
        if (!hasText && !rawIsAnswer && !(blank && blankOutput == BlankOutput.Diagnostic))
            return NodeExecutionResult.Fail(response);

        return NodeExecutionResult.Ok(response, prompt, sessionId, incomingSessionId);
    }

    /// <summary>
    /// The MCP servers a launch is given, whichever way the CLI is told about them: ILD's own when the call's tool selection
    /// has <c>ild</c> and its server can be started, then the provider's custom servers. A call that must have no tools has none.
    /// </summary>
    /// <param name="Ild">Whether the <c>ild</c> server is attached.</param>
    /// <param name="Custom">The provider's custom servers; the parser reserves the <c>ild</c> name, so none can take its place.</param>
    internal sealed record McpServerSet(bool Ild, IReadOnlyList<CustomMcpServer> Custom);

    /// <summary>The servers a launch is given: see <see cref="McpServerSet"/>.</summary>
    /// <param name="traits">The CLI's, which say how an allowlist reads.</param>
    /// <param name="environment">Where the ILD MCP server's DLL is looked for.</param>
    internal static McpServerSet SelectMcpServers(
        AgentAdapterTraits traits, AiProvider provider, IReadOnlyList<string>? allowlist, bool noTools, IProcessEnvironment? environment)
    {
        if (noTools) return new McpServerSet(false, []);
        var ild = AiToolCatalog.NormalizeSelectedToolKeys(traits, allowlist).Contains(AiToolCatalog.Ild, StringComparer.OrdinalIgnoreCase)
            && IldMcpServer.ResolveServerDll(environment) is not null;
        return new McpServerSet(ild, CustomMcpServers.Parse(AiProviderConfig.Parse(provider.Config).CustomMcpServersJson));
    }

    public virtual CredentialPath? CredentialPathFor(AiProvider provider) => null;

    /// <summary>
    /// Opens the execution's right to use the model gateway (ADR-0031): the CLI is then given the lease's
    /// URL and placeholder in place of the provider's credential. Null without DI, where nothing runs for real.
    /// The caller disposes it when the execution ends, which is what ends the token.
    /// </summary>
    private ModelGatewayLease? OpenGateway(AgentExecutionContext ctx)
    {
        if (ScopeFactory is null) return null;
        using var scope = ScopeFactory.CreateScope();
        var registry = scope.ServiceProvider.GetService<IModelGatewayRegistry>();
        var apiUrl = RunHost.GatewayApiUrl(EnvironmentVariables.Get("ILD_API_URL"), ctx.RunContext.WorktreePath);
        // The agent reaches the gateway at ILD's own address, which no block may close.
        var ildHost = Uri.TryCreate(apiUrl, UriKind.Absolute, out var ild) ? ild.Host : null;
        return registry?.Open(
            ctx.Provider.Id,
            OwnerOf(ctx),
            apiUrl,
            GatewayRoutes.ModelHosts(ctx.Provider).Where(host => !string.Equals(host, ildHost, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    /// <summary>
    /// Start a coding-agent CLI. Every agent launch goes through here so that the
    /// run host decides where and how it runs, rather than each call site.
    /// </summary>
    /// <param name="psi">The launch.</param>
    /// <param name="environment">What the adapter sets for this launch (a null value unsets); the launch's whole environment with the execution's, as nothing of <paramref name="psi"/>'s own is read.</param>
    /// <param name="agentFiles">The files in the agent read root that the adapter wrote for this launch and that <paramref name="psi"/> points at; a host that cannot share them stages exactly these.</param>
    /// <param name="ctx">
    /// The execution the launch belongs to, so no launch of it can leave out what it
    /// carries: the provider (the egress proxy applies that provider's scoped
    /// whitelist/blacklist entries to the child's connections), the
    /// <see cref="AgentExecutionContext.Environment"/> set over <paramref name="environment"/>, and the <see cref="AgentExecutionContext.NetworkMode"/> that
    /// decides whether the child is proxied (ADR-0019).
    /// </param>
    protected async Task<IRunProcess> StartAgentProcessAsync(
        ProcessStartInfo psi, AgentExecutionContext ctx, IReadOnlyDictionary<string, string?>? environment = null, IEnumerable<string?>? agentFiles = null)
        => new OutputCappedRunProcess(
            await RunHost.StartCommandAsync(
                RunCommand.Agent(
                    psi, OwnerOf(ctx), ctx.Provider.Id, ctx.NetworkMode, WithExecutionEnvironment(environment, ctx),
                    (ctx.AgentFiles ?? []).Concat(agentFiles ?? []).Where(file => !string.IsNullOrEmpty(file)).Select(file => file!).ToList()) with { RunIn = RunHost.HolderOf(psi.WorkingDirectory) ?? OwnerOf(ctx) },
                ctx.Cancel),
            AgentOutputLimit);

    /// <summary>The most one launch's CLI may write to either of its output streams before it is killed.</summary>
    internal long AgentOutputLimit { get; init; } = RunReadLimits.FromEnvironment().MaxAgentOutputBytes;

    // The execution's environment is set over the adapter's.
    private static Dictionary<string, string?> WithExecutionEnvironment(IReadOnlyDictionary<string, string?>? adapter, AgentExecutionContext ctx)
    {
        var merged = new Dictionary<string, string?>(adapter ?? new Dictionary<string, string?>(), StringComparer.Ordinal);
        foreach (var (name, value) in ctx.Environment ?? new Dictionary<string, string>())
            merged[name] = value;
        return merged;
    }

    /// <summary>The run or chat an execution belongs to: whose processes <see cref="IRunWorkspace.KillAllAsync"/> ends.</summary>
    protected static Guid OwnerOf(AgentExecutionContext ctx) => ctx.ChatSessionId ?? ctx.RunContext.LoopRunId;

    private IRunWorkspace? _runHost;

    /// <summary>
    /// Where the agent's processes and files live: the run host registered with DI, which an adapter built with
    /// services must find. Only one built without services (a unit test's) runs on the local host.
    /// </summary>
    protected IRunWorkspace RunHost => _runHost ??= ResolveRunHost();

    private IRunWorkspace ResolveRunHost()
    {
        if (ScopeFactory is null) return LocalRunHost.Shared;
        using var scope = ScopeFactory.CreateScope();
        return scope.ServiceProvider.GetRunWorkspace();
    }


    /// <summary>
    /// Kill a process and its children. Never throws, but unlike a blind
    /// best-effort kill it reports whether the kill actually took effect.
    /// Silently swallowing a failed kill leaves an orphaned agent writing the
    /// worktree after a Halt or a node timeout while the engine moves on, so
    /// callers surface the failure instead.
    /// </summary>
    /// <returns><c>true</c> when the process is gone (killed, or already exited).</returns>
    protected static bool KillProcessTree(IRunProcess process) => process.TryKill();

    /// <summary>
    /// Kill the agent process tree and build the node's failure message, adding a
    /// diagnostic when the kill did not take (see <see cref="KillProcessTree"/>)
    /// so an un-killable agent is visible in the run instead of silent.
    /// </summary>
    protected static string KillAndDescribe(IRunProcess process, string message)
        => KillProcessTree(process)
            ? message
            : message + " — WARNING: the agent process could not be killed and may still be running.";

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
                if (await SaveSessionFileAsync(store, key, path, format, ctx.Cancel)) return;
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

    /// <summary>True once the file is saved or there was nothing to save; false when an append lost a race and the save should be decided again.</summary>
    private async Task<bool> SaveSessionFileAsync(
        IAdapterSessionSnapshotStore store, SessionSnapshotKey key, string path, string format, CancellationToken ct)
    {
        // The session file sits where the agent can write, so it is only measured and
        // read through the run host (see AgentWritableFiles), never through a link.
        // An empty file holds nothing to keep, and must not replace a history stored earlier.
        if (await RunHost.GetFileLengthAsync(path, ct) is not ({ } length and > 0)) return true;

        var stored = await store.GetMetadataAsync(key, ct);
        if (stored is not null
            && stored.Format == format
            && length >= stored.ByteLength
            && await TailHashAsync(path, stored.ByteLength, ct) == stored.TailHash)
        {
            if (length == stored.ByteLength) return true;

            var tailHash = await TailHashAsync(path, length, ct);
            var result = SessionSnapshotAppendResult.Conflict;
            await RunHost.ReadFileRangeAsync(path, stored.ByteLength, length - stored.ByteLength,
                async added => result = await store.AppendAsync(key, format, added, stored.ByteLength, tailHash, ct));
            return result == SessionSnapshotAppendResult.Appended;
        }

        await RunHost.ReadFileRangeAsync(path, 0, length, whole => store.ReplaceAsync(key, format, whole, ct));
        return true;
    }

    /// <summary>The <see cref="SessionSnapshotFormats.TailHash"/> of the file's bytes just before <paramref name="end"/>.</summary>
    private async Task<string> TailHashAsync(string path, long end, CancellationToken ct)
    {
        var length = Math.Min(end, SessionSnapshotFormats.TailWindow);
        using var tail = new MemoryStream((int)length);
        await RunHost.ReadFileRangeAsync(path, end - length, length, content => content.CopyToAsync(tail, ct));
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
        await RunHost.CreateDirectoryAsync(Path.GetDirectoryName(path)!, ctx.Cancel);
        await RunHost.WriteFileAsync(path, content, ctx.Cancel);
        return true;
    }

    /// <summary>
    /// What <paramref name="parse"/> makes of the session file at <paramref name="path"/>,
    /// read line by line through the run host without holding the file whole; the default when
    /// there is no regular file there.
    /// </summary>
    protected async Task<T?> ParseSessionFileAsync<T>(string path, Func<TextReader, T> parse, CancellationToken ct)
    {
        if (await RunHost.GetFileLengthAsync(path, ct) is not { } length) return default;
        T? result = default;
        await RunHost.ReadFileRangeAsync(path, 0, length, content =>
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
