using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Runs OpenAI's <c>codex</c> CLI headlessly as <c>codex exec --json</c>.
/// Authentication is handled by the CLI itself: the user signs in once from the
/// provider terminal (ChatGPT, "Sign in with Device Code"), which stores
/// credentials under <c>~/.codex</c>, so <see cref="AiProvider.BaseUrl"/> and
/// <see cref="AiProvider.ApiKey"/> are ignored. <see cref="AiProvider.Model"/> is
/// passed as <c>-m</c> when set; blank leaves the CLI's own default.
///
/// Codex runs with <c>--dangerously-bypass-approvals-and-sandbox</c>: it cannot
/// prompt headlessly, and the agent uid and the egress proxy (ADR-0014, ADR-0019)
/// are the sandbox. Of the per-node tool allowlist only <c>ild</c> applies.
///
/// MCP servers are configured per launch with <c>-c mcp_servers.&lt;name&gt;=…</c>
/// overrides, which sit in codex's argv, and codex hands a stdio server only a few
/// variables of its own environment. So no secret goes into an override: the ILD
/// server reads its URL and token from a file named by <c>ILD_MCP_ENV_FILE</c>, and
/// each custom server is started through <c>ild-mcp-server --launch &lt;file&gt;</c>,
/// which replaces itself with the server the file describes, together with its
/// environment and the egress proxy variables (see <see cref="WriteMcpServers"/>).
///
/// Sessions (ADR-0009): codex keeps a thread as a rollout file under
/// <c>~/.codex/sessions</c>, which is what ILD snapshots, restores and forks.
/// </summary>
public sealed class CodexAdapter : CliAgentAdapterBase
{
    private readonly ILogger _logger;

    public CodexAdapter(ILogger<CodexAdapter>? logger = null, IProcessEnvironment? environment = null)
        : base(environment)
    {
        _logger = logger ?? NullLogger<CodexAdapter>.Instance;
    }

    public CodexAdapter(
        IServiceScopeFactory scopeFactory, ILogger<CodexAdapter>? logger = null, IProcessEnvironment? environment = null)
        : base(scopeFactory, environment)
    {
        _logger = logger ?? NullLogger<CodexAdapter>.Instance;
    }

    public override string Name => "Codex";
    public override string[] SupportedProviderTypes => ["codex"];

    public override ConfigFieldDescriptor[] ConfigSchema => [CustomMcpServersField, ExtraArgsField];
    public override AdapterModelSupport ModelSupport => AdapterModelSupport.Optional;
    public override AgentAdapterTraits Traits => TypeTraits;

    public override async Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
    {
        // See TypeTraits.SupportsNoTools: launching anyway would hand a caller
        // that asked for no tools every tool the CLI has.
        if (ctx.NoTools)
            return NodeExecutionResult.Fail(
                "[codex-error] the Codex CLI cannot run without its tools, so it cannot take a call that must have none.");

        IReadOnlyList<CodexMcpServer> mcpServers = [];
        try
        {
            var binaryPath = AiProviderConfig.Parse(ctx.Provider.Config)
                .BinaryPathOr(ManagedAgentInstall.ResolveCommand(ManagedAgentCatalog.Codex, EnvironmentVariables));

            var worktreePath = ctx.RunContext.WorktreePath;
            if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath))
                return NodeExecutionResult.Fail(
                    "[codex-error] AI node requires a valid worktree path; refusing to run outside the loop's worktree.");

            mcpServers = WriteMcpServers(ctx.Provider, ctx.RunContext, ctx.ToolAllowlist, ctx.ChatSessionId, _logger, EnvironmentVariables);

            var sessionsDirectory = SessionsDirectory();
            if (ctx.ManageSession && ScopeFactory is not null && IsSessionId(ctx.SessionId))
            {
                // Fork: seed a copy of the source session under the new id, so the
                // restore below writes the copy and `resume` continues on it. A
                // fork that fails fails the node, as for every adapter; a source
                // with no snapshot leaves nothing to resume, so a new thread starts.
                if (!string.IsNullOrWhiteSpace(ctx.ForkFromSessionId))
                {
                    try
                    {
                        await ForkSessionSnapshotAsync(ctx.RunContext.LoopRunId, ctx.ForkFromSessionId!, ctx.SessionId!, ctx.Cancel);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        return NodeExecutionResult.Fail(
                            $"[codex-error] could not fork the codex session {ctx.ForkFromSessionId} into {ctx.SessionId}: {ex.Message}");
                    }
                }
                if (sessionsDirectory is not null)
                    await TryRestoreRolloutAsync(ctx, sessionsDirectory, ctx.SessionId!);
            }

            var resumeId = await ResumableSessionAsync(ctx.SessionId, sessionsDirectory, ctx.Cancel);

            Process? proc;
            try
            {
                proc = StartAgentProcess(
                    BuildRunProcessStartInfo(binaryPath, worktreePath, ctx.Prompt, resumeId, ctx.AdditionalAllowedDirectories, ctx.Provider.Model, mcpServers, ExtraCliArgs.ForLaunch(ctx.Provider, _logger)),
                    ctx.Provider.Id, ctx.Environment);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                return NodeExecutionResult.Fail(
                    $"[codex-error] cannot start '{binaryPath}' — install or update Codex from the AI Provider page (or make sure the codex CLI is on PATH), and make sure you are signed in (open the provider terminal and sign in with ChatGPT). Details: {ex.Message}");
            }

            using var process = proc ?? throw new InvalidOperationException("Process.Start returned null");

            // codex exec reads stdin to the end before it starts, as more prompt.
            process.StandardInput.Close();

            var stdoutTask = ReadEventsAsync(process.StandardOutput, ctx.ProgressCallback, ctx.OnSessionId, ctx.Cancel);
            var stderrTask = process.StandardError.ReadToEndAsync(ctx.Cancel);

            try
            {
                await process.WaitForExitAsync(ctx.Cancel);
            }
            catch (OperationCanceledException)
            {
                return NodeExecutionResult.Fail(KillAndDescribe(process, "codex timed out"));
            }

            CodexOutput stdout;
            string stderr;
            try
            {
                stdout = await stdoutTask;
                stderr = StripNullChars(await stderrTask);
            }
            catch (OperationCanceledException)
            {
                return NodeExecutionResult.Fail(KillAndDescribe(process, "codex stream read timed out"));
            }

            var response = StripNullChars(string.Join("\n", stdout.Messages));
            if (response.Length == 0 && !stdout.SawJsonEvents)
                response = StripNullChars(stdout.RawStdout.Trim());

            // codex reports a failed turn as an event; the exit code does not say
            // which failure it was, so the event's own message is classified.
            if (stdout.FailureMessage is { } failure)
            {
                var message = StripNullChars(failure);
                return NodeExecutionResult.Fail(
                    $"[codex-error] {message}", response.Length > 0 ? response : null,
                    AiFailureClassifier.ClassifyProviderMessage(message));
            }

            if (process.ExitCode != 0)
                return NodeExecutionResult.Fail($"exit={process.ExitCode} stderr={stderr}", response.Length > 0 ? response : null);

            if (string.IsNullOrWhiteSpace(response))
                return NodeExecutionResult.Fail(!string.IsNullOrWhiteSpace(stderr)
                    ? $"[codex] no assistant text response. stderr: {stderr.Trim()}"
                    : "[codex] no assistant text response from model");

            var sessionId = stdout.ThreadId ?? resumeId;
            var store = ctx.ManageSession && ScopeFactory is not null;
            var resumed = resumeId is not null && (stdout.ThreadId is null || stdout.ThreadId == resumeId);

            var rollout = (store || resumed) && IsSessionId(sessionId) && sessionsDirectory is not null
                ? await TryFindRolloutAsync(sessionsDirectory, sessionId!, ctx.Cancel)
                : null;
            if (store && rollout is not null)
                await PersistSessionFileAsync(ctx, sessionId!, rollout, SessionSnapshotFormats.CodexJsonl, _logger);

            // A new thread counts from zero; a resumed one counts on from its earlier total.
            var usage = !resumed ? AdapterUsageParser.ParseCodex(stdout.RawStdout, NoUsage)
                : await TryReadTotalBeforeLastTurnAsync(rollout, sessionId!, ctx.Cancel) is { } totalBefore
                    ? AdapterUsageParser.ParseCodex(stdout.RawStdout, totalBefore)
                    : null;

            return NodeExecutionResult.Ok(response, ctx.Prompt, sessionId, ctx.IncomingSessionId, usage);
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Fail($"[codex-error] {ex.Message}");
        }
        finally
        {
            DeleteConfigFiles(mcpServers);
        }
    }

    /// <summary>
    /// The flags <see cref="BuildRunProcessStartInfo"/> sets, and those that would
    /// undo what it sets, which a provider's extra CLI arguments may not carry:
    /// <c>--cd</c> moves codex out of the worktree, and <c>--sandbox</c> contradicts
    /// the sandbox bypass. <c>-c</c>/<c>--config</c> is allowed, e.g.
    /// <c>-c model_reasoning_effort=high</c>, except for the config keys ILD sets
    /// itself (see <see cref="TypeTraits"/>).
    /// </summary>
    internal static readonly string[] ReservedCliFlags =
        ["--json", "--skip-git-repo-check", "--dangerously-bypass-approvals-and-sandbox", "--add-dir",
         "-C", "--cd", "-s", "--sandbox"];

    /// <summary>Why each <see cref="ReservedCliFlags"/> entry that ILD does not set itself is refused.</summary>
    internal static readonly Dictionary<string, string> ReservedCliFlagReasons = new(StringComparer.Ordinal)
    {
        ["-C"] = "it would move Codex out of the worktree",
        ["--cd"] = "it would move Codex out of the worktree",
        ["-s"] = "it contradicts how ILD runs Codex (sandbox bypassed; the agent user is the sandbox)",
        ["--sandbox"] = "it contradicts how ILD runs Codex (sandbox bypassed; the agent user is the sandbox)",
    };

    /// <summary>
    /// Codex signs in through its own ChatGPT sign-in and its only tool is <c>ild</c>:
    /// it has no way to turn its own tools off, so it takes no tool-less call, and an
    /// explicit selection without <c>ild</c> is how ILD is turned off. Its
    /// <c>-c</c>/<c>--config key=value</c> overrides any config key; the refused
    /// first dotted segments are what ILD sets itself (a profile could set any of
    /// them), with what they set. Declared after <see cref="ReservedCliFlags"/>,
    /// which it reads.
    /// </summary>
    internal static readonly AgentAdapterTraits TypeTraits = new()
    {
        DisplayName = "Codex",
        CliAuth = new("Codex", "choose **Sign in with Device Code** and sign in with your ChatGPT account"),
        SupportedTools = AiToolCatalog.IldOnlyTools,
        EmptyToolSelectionMeansNone = true,
        SupportsNoTools = false,
        ExtraCliArgs = new()
        {
            ReservedFlags = ReservedCliFlags,
            ReservedFlagReasons = ReservedCliFlagReasons,
            RefusedConfigKeys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mcp_servers"] = "Codex's MCP servers",
                ["sandbox_mode"] = "Codex's sandbox",
                ["sandbox_workspace_write"] = "Codex's sandbox",
                ["approval_policy"] = "Codex's approvals",
                ["profiles"] = "Codex's profiles",
            },
            ModelHasShortFlag = true,
            AttachedShortValues = true,
        },
    };

    /// <summary>
    /// <c>codex exec</c> with ILD's options, then the provider's extra arguments,
    /// then <c>resume &lt;id&gt;</c> when a session continues (the options before
    /// the subcommand apply to it), and the prompt after <c>--</c>. stdin is
    /// redirected so the caller can close it.
    /// </summary>
    public static ProcessStartInfo BuildRunProcessStartInfo(
        string binaryPath,
        string worktreePath,
        string prompt,
        string? sessionId = null,
        IReadOnlyList<string>? additionalAllowedDirectories = null,
        string? model = null,
        IReadOnlyList<CodexMcpServer>? mcpServers = null,
        IReadOnlyList<string>? extraArgs = null)
    {
        var psi = new ProcessStartInfo(binaryPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = worktreePath,
        };

        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("--skip-git-repo-check");
        psi.ArgumentList.Add("--dangerously-bypass-approvals-and-sandbox");
        psi.ArgumentList.Add("--add-dir");
        psi.ArgumentList.Add(worktreePath);

        // Per-turn extra grants (ADR-0011), each its own --add-dir; the worktree
        // itself is already added.
        foreach (var dir in additionalAllowedDirectories ?? [])
        {
            if (string.IsNullOrWhiteSpace(dir) || string.Equals(dir, worktreePath, StringComparison.Ordinal))
                continue;
            psi.ArgumentList.Add("--add-dir");
            psi.ArgumentList.Add(dir);
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(model);
        }

        foreach (var server in mcpServers ?? [])
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"mcp_servers.{server.Name}={server.Table}");
        }

        foreach (var arg in extraArgs ?? [])
            psi.ArgumentList.Add(arg);

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            psi.ArgumentList.Add("resume");
            psi.ArgumentList.Add(sessionId);
        }

        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(prompt);
        return psi;
    }

    /// <summary>
    /// Codex's MCP server settings for one launch: the <c>ild</c> server when that
    /// tool is selected, and every provider-scoped custom server whether or not it
    /// is (as for Copilot). Each server's secrets go into a file written by
    /// <see cref="IldMcpServer.TryWriteConfigFile"/>, which the caller deletes once
    /// codex exits; its codex override names only the ILD MCP server DLL and that
    /// file. A custom server is skipped, with a warning naming it, when its name
    /// cannot be a codex config key or there is no ILD MCP server DLL to launch it
    /// with; any server is skipped when its file cannot be written.
    /// </summary>
    /// <param name="environment">
    /// Where the ILD MCP server, the agent read root and the egress proxy are
    /// configured; the process environment by default.
    /// </param>
    public static IReadOnlyList<CodexMcpServer> WriteMcpServers(
        AiProvider provider, LoopRunContext runContext, IReadOnlyList<string>? allowlist, Guid? chatSessionId = null,
        ILogger? logger = null, IProcessEnvironment? environment = null)
    {
        logger ??= NullLogger.Instance;
        environment ??= ProcessEnvironment.Current;
        var servers = new List<CodexMcpServer>();
        try
        {
            var dll = IldMcpServer.ResolveServerDll(environment);

            // codex waits for a server before the first model request only when it is
            // required, so without that a slow start leaves the turn without ILD's
            // tools; a required server that cannot start fails the run, saying why.
            // The call timeout sits above the server's own 100 s HTTP timeout, since
            // codex's 60 s default would cut a long call, like a clone, short.
            var enabledKeys = AiToolCatalog.NormalizeSelectedToolKeys(TypeTraits, allowlist);
            if (dll is not null
                && enabledKeys.Contains(AiToolCatalog.Ild, StringComparer.OrdinalIgnoreCase)
                && IldMcpServer.TryWriteConfigFile(
                    "ild-codex-mcp", runContext.LoopRunId, IldMcpServer.BuildEnvironment(runContext, chatSessionId, environment),
                    logger, environment) is { } settingsFile)
                servers.Add(new("ild", settingsFile,
                    $"{{command=\"dotnet\",args=[{TomlString(dll)}],env={{ILD_MCP_ENV_FILE={TomlString(settingsFile)}}},"
                    + "required=true,startup_timeout_sec=30,tool_timeout_sec=120}"));

            // The parser reserves the "ild" name, so these can never clobber it.
            var customServers = CustomMcpServers.Parse(AiProviderConfig.Parse(provider.Config).CustomMcpServersJson);
            if (customServers.Count == 0)
                return servers;

            var proxyUrl = AgentIsolation.ResolveEgressProxyUrl(environment.Get(AgentIsolation.EgressProxyPortEnvVar), provider.Id);
            foreach (var server in customServers)
            {
                if (!ConfigKeyName.IsMatch(server.Name) || dll is null)
                {
                    logger.LogWarning(
                        "Codex runs without the custom MCP server {ServerName} of AI provider {ProviderName}: {Reason}",
                        server.Name, provider.Name,
                        dll is null
                            ? "the ILD MCP server, which launches it, cannot be found."
                            : "codex only takes a server name made of letters, digits, '-' and '_'.");
                    continue;
                }

                var launch = CustomMcpServers.BuildCustomMcpEntry(server);
                var env = proxyUrl is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(AgentIsolation.EgressProxyEnvironment(proxyUrl));
                foreach (var (name, value) in server.Env)
                    env[name] = value;
                launch["env"] = env;

                if (IldMcpServer.TryWriteConfigFile("ild-codex-mcp-server", runContext.LoopRunId, launch, logger, environment) is { } launchFile)
                    servers.Add(new(server.Name, launchFile,
                        $"{{command=\"dotnet\",args=[{TomlString(dll)},\"--launch\",{TomlString(launchFile)}]}}"));
            }

            return servers;
        }
        catch
        {
            DeleteConfigFiles(servers);
            throw;
        }
    }

    /// <summary>Delete the files <see cref="WriteMcpServers"/> wrote, best effort.</summary>
    public static void DeleteConfigFiles(IEnumerable<CodexMcpServer> servers)
    {
        foreach (var server in servers)
        {
            try { File.Delete(server.ConfigFile); } catch { /* best effort */ }
        }
    }

    /// <summary>A name codex accepts unquoted in <c>-c mcp_servers.&lt;name&gt;=…</c>.</summary>
    private static readonly Regex ConfigKeyName = new("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);

    /// <summary><paramref name="value"/> as a TOML basic string.</summary>
    private static string TomlString(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c is '"' or '\\')
                sb.Append('\\').Append(c);
            else if (c < ' ' || c == '\u007f')
                sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else
                sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    private static async Task<CodexOutput> ReadEventsAsync(
        StreamReader reader,
        Func<string, Task>? progressCallback,
        Action<string>? onSessionId,
        CancellationToken ct)
    {
        var raw = new StringBuilder();
        var messages = new List<string>();
        string? threadId = null;
        string? turnFailure = null;
        string? lastError = null;
        var turnCompleted = false;
        var sawJsonEvents = false;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            raw.AppendLine(line);

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;
                sawJsonEvents = true;

                // The `codex exec --json` events (codex-rs/exec/src/exec_events.rs):
                //   { "type": "thread.started", "thread_id": … }
                //   { "type": "item.started" | "item.completed", "item": { "id", "type", … } }
                //   { "type": "turn.completed", "usage": … } | { "type": "turn.failed", "error": { "message" } }
                //   { "type": "error", "message": … }   (also "Reconnecting…" notices mid-turn)
                // An item of type "error" is a warning, not a failure.
                var item = root.TryGetProperty("item", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
                switch (GetString(root, "type"))
                {
                    case "thread.started" when threadId is null && GetString(root, "thread_id") is { Length: > 0 } id:
                        threadId = id;
                        FireSessionId(onSessionId, id);
                        break;
                    case "item.completed" when GetString(item, "type") == "agent_message"
                                               && GetString(item, "text") is { Length: > 0 } text:
                        messages.Add(text);
                        if (progressCallback is not null)
                            await progressCallback(text).ConfigureAwait(false);
                        break;
                    case "item.started" when progressCallback is not null
                                             && GetString(item, "type") is { } itemType && itemType != "agent_message":
                        await progressCallback(ToolMarker(itemType, item)).ConfigureAwait(false);
                        break;
                    case "turn.completed":
                        turnCompleted = true;
                        break;
                    case "turn.failed":
                        turnFailure = root.TryGetProperty("error", out var error) ? GetString(error, "message") : null;
                        turnFailure ??= "the turn failed";
                        break;
                    case "error":
                        lastError = GetString(root, "message") ?? "codex reported an error";
                        break;
                }
            }
        }

        return new CodexOutput(
            raw.ToString(), messages, threadId, turnFailure ?? (turnCompleted ? null : lastError), sawJsonEvents);
    }

    private static string ToolMarker(string itemType, JsonElement item)
        => itemType == "mcp_tool_call"
            ? ToolMarkerFormatter.Format(GetString(item, "tool"), item.TryGetProperty("arguments", out var arguments) ? arguments : default)
            : ToolMarkerFormatter.Format(itemType, item);

    // ---- sessions ----

    // Thread ids are UUIDs. Anything else is not looked up, since it becomes part
    // of a file name and a find pattern.
    private static readonly Regex SessionIdPattern = new("^[A-Za-z0-9-]+$", RegexOptions.CultureInvariant);

    private static bool IsSessionId(string? sessionId) => sessionId is not null && SessionIdPattern.IsMatch(sessionId);

    // codex keeps its sessions under the HOME it runs with, which under uid
    // isolation is the agent's (AgentIsolation.ResolveChildHome), not ours.
    private string? SessionsDirectory()
    {
        var home = AgentIsolation.ResolveChildHome(AgentIsolation.AgentUser, AgentIsolation.AgentHome)
            ?? EnvironmentVariables.Get("HOME");
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".codex", "sessions");
    }

    // The rollouts live in the shared credential store, which the agent can write,
    // so they are only listed, read and written as the agent (see AgentWritableFiles).
    private static async Task<string?> FindRolloutAsync(string sessionsDirectory, string sessionId, CancellationToken ct)
        => (await AgentWritableFiles.ListFilesAsync(sessionsDirectory, $"rollout-*-{sessionId}.jsonl", ct))
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .LastOrDefault();

    private static readonly TokenUsage NoUsage = new(0, 0, null);

    /// <summary>
    /// <paramref name="sessionId"/> when codex has a rollout to resume it from;
    /// otherwise null, with a warning, so the run starts a new thread rather than
    /// handing codex an id it does not know.
    /// </summary>
    private async Task<string?> ResumableSessionAsync(string? sessionId, string? sessionsDirectory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        if (sessionsDirectory is null)
        {
            _logger.LogWarning("Codex cannot look for the session {SessionId} without a HOME, so it starts a new one.", sessionId);
            return null;
        }
        try
        {
            if (IsSessionId(sessionId) && await FindRolloutAsync(sessionsDirectory, sessionId, ct) is not null)
                return sessionId;
            _logger.LogWarning("Codex has no session {SessionId} to resume, so it starts a new one.", sessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not look for the codex session {SessionId}, so it starts a new one: {Reason}", sessionId, ex.Message);
        }
        return null;
    }

    /// <summary>The rollout file of <paramref name="sessionId"/>, or null when there is none or it cannot be looked for (logged).</summary>
    private async Task<string?> TryFindRolloutAsync(string sessionsDirectory, string sessionId, CancellationToken ct)
    {
        try
        {
            return await FindRolloutAsync(sessionsDirectory, sessionId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the codex session {SessionId}: {Reason}", sessionId, ex.Message);
            return null;
        }
    }

    /// <summary>The total the thread had before this turn, read from its rollout; null when there is none or it cannot be read (logged).</summary>
    private async Task<TokenUsage?> TryReadTotalBeforeLastTurnAsync(string? rollout, string sessionId, CancellationToken ct)
    {
        if (rollout is null) return null;
        try
        {
            return await ParseSessionFileAsync(rollout, AdapterUsageParser.ParseCodexTotalBeforeLastTurn, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the codex session {SessionId}: {Reason}", sessionId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Write the stored rollout back when codex has none, so `resume` finds it. This must
    /// not decide the turn: a restore that fails, or is cancelled, is logged and a new
    /// thread starts; a cancelled turn still stops at codex's own wait, which kills it.
    /// </summary>
    private async Task TryRestoreRolloutAsync(AgentExecutionContext ctx, string sessionsDirectory, string sessionId)
    {
        try
        {
            if (await FindRolloutAsync(sessionsDirectory, sessionId, ctx.Cancel) is not null) return;

            // codex resumes only a rollout named the way it names them.
            var now = DateTime.UtcNow;
            var rollout = Path.Combine(
                sessionsDirectory,
                now.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture),
                $"rollout-{now.ToString("yyyy-MM-dd'T'HH-mm-ss", CultureInfo.InvariantCulture)}-{sessionId}.jsonl");
            await RestoreSessionFileAsync(ctx, sessionId, rollout, SessionSnapshotFormats.CodexJsonl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not restore the codex session {SessionId}: {Reason}", sessionId, ex.Message);
        }
    }

    private sealed record CodexOutput(
        string RawStdout,
        IReadOnlyList<string> Messages,
        string? ThreadId,
        string? FailureMessage,
        bool SawJsonEvents);
}

/// <summary>
/// One MCP server for a codex launch: its name, the file holding its secrets
/// (deleted when the launch ends), and the TOML inline table codex is given.
/// </summary>
public sealed record CodexMcpServer(string Name, string ConfigFile, string Table);
