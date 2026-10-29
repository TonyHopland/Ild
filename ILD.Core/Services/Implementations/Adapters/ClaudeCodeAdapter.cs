using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
/// Runs Anthropic's <c>claude</c> CLI in headless mode. Authentication is
/// expected to be set up out-of-band (e.g. <c>claude /login</c> for a Max
/// subscription, which writes credentials to <c>~/.claude</c>). The adapter
/// intentionally ignores <see cref="AiProvider.BaseUrl"/> and
/// <see cref="AiProvider.ApiKey"/> — those fields are not meaningful for
/// subscription-based auth. <see cref="AiProvider.Model"/> is honoured, since
/// choosing Opus over Sonnet is exactly a choice a subscription user makes: it
/// is passed as <c>--model</c> when set, and omitted when blank so the CLI picks
/// its own default.
/// </summary>
public sealed class ClaudeCodeAdapter : CliAgentAdapterBase
{
    private readonly ILogger _logger;

    public ClaudeCodeAdapter(ILogger<ClaudeCodeAdapter>? logger = null, IProcessEnvironment? environment = null)
        : base(environment)
    {
        _logger = logger ?? NullLogger<ClaudeCodeAdapter>.Instance;
    }

    public ClaudeCodeAdapter(
        IServiceScopeFactory scopeFactory, ILogger<ClaudeCodeAdapter>? logger = null, IProcessEnvironment? environment = null)
        : base(scopeFactory, environment)
    {
        _logger = logger ?? NullLogger<ClaudeCodeAdapter>.Instance;
    }

    public override string Name => "ClaudeCode";
    public override string[] SupportedProviderTypes => ["claude-code"];
    public override ConfigFieldDescriptor[] ConfigSchema => [CustomMcpServersField, ExtraArgsField];
    public override AdapterModelSupport ModelSupport => AdapterModelSupport.Optional;
    public override AgentAdapterTraits Traits => TypeTraits;

    public override async Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
    {
        string? mcpConfigPath = null;
        try
        {
            var binaryPath = AiProviderConfig.Parse(ctx.Provider.Config)
                .BinaryPathOr(ManagedAgentInstall.ResolveCommand(ManagedAgentCatalog.ClaudeCode, EnvironmentVariables));

            var worktreePath = ctx.RunContext.WorktreePath;
            if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath))
                return NodeExecutionResult.Fail(
                    "[claude-code-error] AI node requires a valid worktree path; refusing to run outside the loop's worktree.");

            // Inject the ILD MCP server entry so the headless `claude` process
            // can call list/create tools through the agent-scoped API. The CLI
            // accepts a JSON config via `--mcp-config <file>`, which we merge
            // with whatever the user has installed in their config — there is
            // no replace-only mode required here.
            if (!ctx.NoTools)
                mcpConfigPath = TryWriteIldMcpConfig(ctx.Provider, ctx.RunContext, ctx.ToolAllowlist, ctx.ChatSessionId, _logger, EnvironmentVariables);

            // Fork: seed a copy of the source session's transcript under the
            // destination id (leaving the source file untouched) so the restore
            // below rehydrates the copy and `--resume` continues on the fork.
            if (ctx.ManageSession && !string.IsNullOrWhiteSpace(ctx.SessionId) && !string.IsNullOrWhiteSpace(ctx.ForkFromSessionId))
                await ForkSessionSnapshotAsync(ctx.RunContext.LoopRunId, ctx.ForkFromSessionId!, ctx.SessionId!, ctx.Cancel);

            // Before invoking claude, if a managed session is in play and the
            // turn JSONL is missing from $HOME/.claude/projects, materialize
            // it from the snapshot store so `--resume` can pick up the
            // history even when the on-disk cache has been wiped.
            if (ctx.ManageSession && !string.IsNullOrWhiteSpace(ctx.SessionId))
                await TryRestoreSessionJsonlAsync(ctx, ctx.SessionId!, worktreePath);

            // Read after the fork and restore, as it is what `--resume` restores from.
            var sessionCostBefore = string.IsNullOrWhiteSpace(ctx.SessionId)
                ? 0m
                : await ReadSessionCostBeforeAsync(ctx.SessionId!, worktreePath, ctx.Cancel);

            Process? proc;
            try
            {
                proc = StartAgentProcess(
                    BuildRunProcessStartInfo(binaryPath, worktreePath, ctx.Prompt, ctx.SessionId, mcpConfigPath, ctx.AdditionalAllowedDirectories, ctx.Provider.Model, ctx.NoTools, ExtraCliArgs.ForLaunch(ctx.Provider, _logger)),
                    ctx);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                return NodeExecutionResult.Fail(
                    $"[claude-code-error] cannot start '{binaryPath}' — install or update Claude Code from the AI Provider page (or make sure the claude CLI is on PATH), and make sure you are logged in (e.g. via 'claude /login'). Details: {ex.Message}");
            }

            using var process = proc ?? throw new InvalidOperationException("Process.Start returned null");

            var stdoutTask = ReadStreamJsonAsync(process.StandardOutput, ctx.ProgressCallback, ctx.OnSessionId, ctx.Cancel);
            var stderrTask = process.StandardError.ReadToEndAsync(ctx.Cancel);

            try
            {
                await process.WaitForExitAsync(ctx.Cancel);
            }
            catch (OperationCanceledException)
            {
                return NodeExecutionResult.Fail(KillAndDescribe(process, "claude-code timed out"));
            }

            ClaudeStreamOutput stdout;
            string stderr;
            try
            {
                stdout = await stdoutTask;
                stderr = await stderrTask;
            }
            catch (OperationCanceledException)
            {
                return NodeExecutionResult.Fail(KillAndDescribe(process, "claude-code stream read timed out"));
            }

            var result = AssembleResult(stdout, stderr, process.ExitCode, ctx.Prompt, ctx.SessionId, ctx.IncomingSessionId, sessionCostBefore);

            // On a successful managed-session turn, copy the JSONL claude
            // wrote at $HOME/.claude/projects/<encoded-cwd>/<sid>.jsonl into
            // the snapshot store. This is what surfaces the run's session in
            // the "Available AI Sessions" panel and lets us rehydrate the
            // on-disk file on a later turn (see TryRestoreSessionJsonlAsync).
            if (result.Success && ctx.ManageSession && !string.IsNullOrWhiteSpace(result.SessionId))
                await TryPersistSessionJsonlAsync(ctx, result.SessionId!, worktreePath);

            return result;
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Fail($"[claude-code-error] {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(mcpConfigPath))
            {
                try { File.Delete(mcpConfigPath); } catch { /* best effort */ }
            }
        }
    }

    /// <summary>Turns a finished run's parsed output, stderr and exit code into the node's result.</summary>
    internal static NodeExecutionResult AssembleResult(
        ClaudeStreamOutput stdout, string stderr, int exitCode, string prompt,
        string? requestedSessionId, string? incomingSessionId, decimal sessionCostBefore)
    {
        var effectiveSessionId = stdout.SessionId ?? requestedSessionId;
        var response = stdout.Content;

        if (string.IsNullOrWhiteSpace(response))
        {
            if (!stdout.SawJsonEvents)
                response = stdout.RawStdout;
            else if (!string.IsNullOrWhiteSpace(stdout.ErrorMessage))
                response = $"[claude-code-error] {stdout.ErrorMessage}";
            else if (!string.IsNullOrWhiteSpace(stderr))
                response = $"[claude-code] no assistant text response. stderr: {stderr.Trim()}";
            else
                response = "[claude-code] no assistant text response from model";
        }

        if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout.ErrorMessage))
            // The result event's own error text is the provider's message,
            // isolated — classify from it (same rationale as OpenCodeAdapter).
            return NodeExecutionResult.Fail(
                $"claude-code session error: {stdout.ErrorMessage}", response,
                AiFailureClassifier.ClassifyProviderMessage(stdout.ErrorMessage));

        if (exitCode == 0 && stdout.SawJsonEvents && string.IsNullOrWhiteSpace(stdout.Content))
            return NodeExecutionResult.Fail(response);

        // The earlier total only counts when Claude answered on the session it was asked to resume.
        var costBeforeThisRun = stdout.SessionId is null || stdout.SessionId == requestedSessionId ? sessionCostBefore : 0m;

        return exitCode == 0
            ? NodeExecutionResult.Ok(response, prompt, effectiveSessionId, incomingSessionId, AdapterUsageParser.ParseClaude(stdout.RawStdout, costBeforeThisRun))
            : NodeExecutionResult.Fail($"exit={exitCode} stderr={stderr}", response);
    }

    /// <summary>The flags <see cref="BuildRunProcessStartInfo"/> sets, which a provider's extra CLI arguments may not repeat.</summary>
    internal static readonly string[] ReservedCliFlags =
        ["--print", "-p", "--output-format", "--verbose", "--tools", "--strict-mcp-config", "--no-session-persistence",
         "--add-dir", "--permission-mode", "--mcp-config", "--resume", "-r"];

    /// <summary>
    /// Claude Code signs in through its own <c>/login</c> and offers every workspace
    /// tool. Declared after <see cref="ReservedCliFlags"/>, which it reads.
    /// </summary>
    internal static readonly AgentAdapterTraits TypeTraits = new()
    {
        DisplayName = "Claude Code",
        CliAuth = new("Claude Code", "run the `/login` slash command to sign in (with an active Claude Code subscription)"),
        SupportedTools = AiToolCatalog.DefaultTools,
        EmptyToolSelectionMeansNone = false,
        SupportsNoTools = true,
        ExtraCliArgs = ExtraCliArgsRules.None with { ReservedFlags = ReservedCliFlags },
    };

    public static ProcessStartInfo BuildRunProcessStartInfo(
        string binaryPath,
        string worktreePath,
        string prompt,
        string? sessionId,
        string? mcpConfigPath = null,
        IReadOnlyList<string>? additionalAllowedDirectories = null,
        string? model = null,
        bool noTools = false,
        IReadOnlyList<string>? extraArgs = null)
    {
        var psi = new ProcessStartInfo(binaryPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = worktreePath,
        };

        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");

        if (noTools)
        {
            // With no tools there is nothing to grant: no --add-dir, no permission bypass.
            psi.ArgumentList.Add("--tools");
            psi.ArgumentList.Add("");
            psi.ArgumentList.Add("--strict-mcp-config");
            psi.ArgumentList.Add("--no-session-persistence");
        }
        else
        {
            psi.ArgumentList.Add("--add-dir");
            psi.ArgumentList.Add(worktreePath);

            // Per-turn extra grants (ADR-0011): e.g. the Chat Context's open work
            // item active-run worktree. Each becomes its own `--add-dir` so the
            // agent can reach the absolute path without changing its cwd. Skip the
            // worktree itself (already added) and any non-existent path.
            if (additionalAllowedDirectories is not null)
            {
                foreach (var dir in additionalAllowedDirectories)
                {
                    if (string.IsNullOrWhiteSpace(dir)
                        || string.Equals(dir, worktreePath, StringComparison.Ordinal))
                        continue;
                    psi.ArgumentList.Add("--add-dir");
                    psi.ArgumentList.Add(dir);
                }
            }

            psi.ArgumentList.Add("--permission-mode");
            psi.ArgumentList.Add("bypassPermissions");
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
        }

        if (!string.IsNullOrWhiteSpace(mcpConfigPath))
        {
            psi.ArgumentList.Add("--mcp-config");
            psi.ArgumentList.Add(mcpConfigPath);
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(sessionId);
        }

        foreach (var arg in extraArgs ?? [])
            psi.ArgumentList.Add(arg);

        // Terminate option parsing before the prompt. `--mcp-config` is a
        // variadic option in the claude CLI: without this separator it greedily
        // consumes the trailing prompt as another config-file path, which the
        // CLI then fails to open (ENAMETOOLONG). The `--` guarantees the prompt
        // is treated as the positional prompt argument regardless of whether a
        // `--resume <id>` precedes it.
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(prompt);
        return psi;
    }

    /// <summary>
    /// Build the <c>mcpServers.ild</c> entry pointing at the ILD MCP server,
    /// in the shape Claude Code's <c>--mcp-config</c> file expects:
    /// <c>{ "command": "dotnet", "args": ["…/ild-mcp-server.dll"], "env": {…} }</c>.
    /// Returns <c>null</c> when no server DLL can be located, in which case
    /// the entry is omitted (failing open rather than poisoning the config).
    /// </summary>
    /// <param name="environment">Where the server's DLL, URL and token are configured; the process environment by default.</param>
    public static Dictionary<string, object?>? BuildIldMcpEntry(
        LoopRunContext? runContext, Guid? chatSessionId = null, IProcessEnvironment? environment = null)
    {
        var dllPath = IldMcpServer.ResolveServerDll(environment);
        if (dllPath == null) return null;

        return new Dictionary<string, object?>
        {
            ["command"] = "dotnet",
            ["args"] = new[] { dllPath },
            ["env"] = IldMcpServer.BuildEnvironment(runContext, chatSessionId, environment),
        };
    }

    /// <summary>
    /// Write the MCP config, which carries the ILD API token, to a JSON file (see
    /// <see cref="IldMcpServer.TryWriteConfigFile"/>) the caller can pass to
    /// <c>claude --mcp-config</c>. Merges the built-in <c>ild</c> server (only
    /// when that tool is in the allowlist) with any provider-scoped custom MCP
    /// servers, which apply for every repo this provider runs in and are written
    /// even when <c>ild</c> is disabled. Returns <c>null</c> when there is nothing
    /// to write (no ild entry and no custom servers), which is not logged, or the
    /// temp file can't be written, which is logged to <paramref name="logger"/>.
    /// </summary>
    /// <param name="environment">
    /// Where the <c>ild</c> server and the agent read root are configured; the
    /// process environment by default.
    /// </param>
    public static string? TryWriteIldMcpConfig(
        AiProvider provider, LoopRunContext runContext, IReadOnlyList<string>? toolAllowlist, Guid? chatSessionId = null,
        ILogger? logger = null, IProcessEnvironment? environment = null)
    {
        var servers = new Dictionary<string, object?>();

        var enabledKeys = AiToolCatalog.NormalizeSelectedToolKeys(TypeTraits, toolAllowlist);
        if (enabledKeys.Contains(AiToolCatalog.Ild, StringComparer.OrdinalIgnoreCase))
        {
            var entry = BuildIldMcpEntry(runContext, chatSessionId, environment);
            if (entry != null)
                servers["ild"] = entry;
        }

        // Provider-scoped custom MCP servers. The parser reserves the "ild" name,
        // so these can never clobber the entry above.
        foreach (var server in CustomMcpServers.Parse(AiProviderConfig.Parse(provider.Config).CustomMcpServersJson))
            servers[server.Name] = CustomMcpServers.BuildCustomMcpEntry(server);

        return servers.Count == 0
            ? null
            : IldMcpServer.TryWriteConfigFile(
                "ild-claude-mcp", runContext.LoopRunId, new Dictionary<string, object?> { ["mcpServers"] = servers },
                logger ?? NullLogger.Instance, environment);
    }

    internal static async Task<ClaudeStreamOutput> ReadStreamJsonAsync(
        StreamReader reader,
        Func<string, Task>? progressCallback,
        Action<string>? onSessionId,
        CancellationToken ct)
    {
        var raw = new StringBuilder();
        var content = new StringBuilder();
        string? sessionId = null;
        string? errorMessage = null;
        var sawJsonEvents = false;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            raw.AppendLine(line);

            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            JsonDocument? doc;
            try
            {
                doc = JsonDocument.Parse(trimmed);
            }
            catch (JsonException)
            {
                continue;
            }

            sawJsonEvents = true;
            using var jsonDoc = doc;
            var root = jsonDoc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                continue;

            // `claude --output-format stream-json` emits these top-level event
            // shapes (see https://docs.anthropic.com/.../claude-code/sdk):
            //   { "type": "system", "subtype": "init", "session_id": ... }
            //   { "type": "assistant", "message": { "content": [...] } }
            //   { "type": "user",      "message": { "content": [...] } }   (tool results)
            //   { "type": "result",    "subtype": "...", "result": ..., "session_id": ..., "is_error": bool }
            if (TryGetString(root, "session_id", out var sid) && !string.IsNullOrWhiteSpace(sid))
            {
                var isFirst = sessionId is null;
                sessionId = sid;
                if (isFirst) FireSessionId(onSessionId, sid);
            }

            if (!TryGetString(root, "type", out var eventType))
                continue;

            if (string.Equals(eventType, "assistant", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object)
            {
                var text = ExtractAssistantText(message);
                if (!string.IsNullOrEmpty(text))
                {
                    content.Append(text);
                    if (progressCallback is not null)
                        await progressCallback(text).ConfigureAwait(false);
                }

                // Tool calls are part of the run's complete picture but not of
                // the node's text output: surface them on the live stream only.
                if (progressCallback is not null)
                {
                    foreach (var marker in ExtractToolMarkers(message))
                        await progressCallback(marker).ConfigureAwait(false);
                }
            }
            else if (string.Equals(eventType, "result", StringComparison.OrdinalIgnoreCase))
            {
                var isError = root.TryGetProperty("is_error", out var errFlag)
                    && errFlag.ValueKind == JsonValueKind.True;
                if (isError && TryGetString(root, "result", out var errResult) && !string.IsNullOrWhiteSpace(errResult))
                    errorMessage = errResult;
                else if (TryGetString(root, "subtype", out var subtype)
                    && subtype is "error_max_turns" or "error_during_execution")
                    errorMessage = subtype;
            }
        }

        return new ClaudeStreamOutput(raw.ToString(), content.ToString(), sessionId, errorMessage, sawJsonEvents);
    }

    private static string? ExtractAssistantText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
            return null;

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();

        if (content.ValueKind != JsonValueKind.Array)
            return null;

        var sb = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            // assistant content parts: { "type": "text", "text": "..." } and
            // tool_use parts which we ignore for display.
            if (TryGetString(item, "type", out var partType)
                && !string.Equals(partType, "text", StringComparison.OrdinalIgnoreCase))
                continue;

            if (TryGetString(item, "text", out var text) && !string.IsNullOrEmpty(text))
                sb.Append(text);
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>
    /// Build readable markers for any <c>tool_use</c> parts in an assistant
    /// message (e.g. <c>\n[tool: Bash] npm test\n</c>). These feed the live view so
    /// the run's tool activity is visible, without polluting the node's text output.
    /// </summary>
    private static IEnumerable<string> ExtractToolMarkers(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (!TryGetString(item, "type", out var partType)
                || !string.Equals(partType, "tool_use", StringComparison.OrdinalIgnoreCase))
                continue;

            var arguments = item.TryGetProperty("input", out var input) ? input : default;
            yield return $"\n{ToolMarkerFormatter.Format(GetString(item, "name"), arguments)}\n";
        }
    }

    private async Task TryRestoreSessionJsonlAsync(AgentExecutionContext ctx, string sessionId, string worktreePath)
    {
        if (ScopeFactory is null) return;

        // Claude keeps its session files in the shared credential store, which the
        // agent can write, so they are only checked, written and read as the agent
        // (see AgentWritableFiles), never by the orchestrator through a link.
        var path = GetSessionFilePath(worktreePath, sessionId, AgentIsolation.AgentUser, AgentIsolation.AgentHome, EnvironmentVariables);
        if (path is null || await AgentWritableFiles.FileExistsAsync(path, ctx.Cancel)) return;

        try
        {
            await RestoreSessionFileAsync(ctx, sessionId, path, SessionSnapshotFormats.ClaudeJsonl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not restore Claude session {SessionId}, so it resumes without its saved history: {Reason}", sessionId, ex.Message);
        }
    }

    /// <summary>The cost the session's transcript says it reached, or zero when there is none or it cannot be read (logged).</summary>
    private async Task<decimal> ReadSessionCostBeforeAsync(string sessionId, string worktreePath, CancellationToken ct)
    {
        var path = GetSessionFilePath(worktreePath, sessionId, AgentIsolation.AgentUser, AgentIsolation.AgentHome, EnvironmentVariables);
        if (path is null) return 0m;

        try
        {
            return await ParseSessionFileAsync(path, transcript => AdapterUsageParser.ParseClaudeSessionCostBefore(transcript, sessionId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the cost of Claude session {SessionId}, so its total is recorded as it is: {Reason}", sessionId, ex.Message);
            return 0m;
        }
    }

    private async Task TryPersistSessionJsonlAsync(AgentExecutionContext ctx, string sessionId, string worktreePath)
    {
        var path = GetSessionFilePath(worktreePath, sessionId, AgentIsolation.AgentUser, AgentIsolation.AgentHome, EnvironmentVariables);
        if (path is null) return;

        await PersistSessionFileAsync(ctx, sessionId, path, SessionSnapshotFormats.ClaudeJsonl, _logger);
    }

    // Claude keeps its sessions under the HOME it runs with, which under uid
    // isolation is the agent's (AgentIsolation.ResolveChildHome), not ours.
    public static string? GetSessionFilePath(string worktreePath, string sessionId)
        => GetSessionFilePath(worktreePath, sessionId, AgentIsolation.AgentUser, AgentIsolation.AgentHome, ProcessEnvironment.Current);

    /// <inheritdoc cref="GetSessionFilePath(string, string)"/>
    /// <param name="environment">Where our own <c>HOME</c> is read.</param>
    internal static string? GetSessionFilePath(
        string worktreePath, string sessionId, string? agentUser, string? agentHome, IProcessEnvironment environment)
    {
        if (string.IsNullOrEmpty(worktreePath) || string.IsNullOrEmpty(sessionId)) return null;
        return GetProjectsRoot(agentUser, agentHome, environment) is { } projects
            ? Path.Combine(projects, EncodeWorktreePath(worktreePath), $"{sessionId}.jsonl")
            : null;
    }

    /// <summary>The <c>.claude/projects</c> directory under the <c>HOME</c> Claude runs with.</summary>
    internal static string? GetProjectsRoot(string? agentUser, string? agentHome, IProcessEnvironment environment)
    {
        var home = AgentIsolation.ResolveChildHome(agentUser, agentHome) ?? environment.Get("HOME");
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".claude", "projects");
    }

    // Claude Code stores per-cwd session JSONL files under
    // ~/.claude/projects/<encoded-cwd>/. The encoding maps both path separators
    // and dots to dashes (e.g. /home/ild/wi-1.2 -> -home-ild-wi-1-2). Mapping
    // only '/' left dotted worktree paths pointing at the wrong directory, so
    // snapshot persist/restore silently no-op'd. If Anthropic ever changes the
    // rule, restore/persist still no-op (path miss) rather than misbehave.
    public static string EncodeWorktreePath(string path) => path.Replace('/', '-').Replace('.', '-');

    internal sealed record ClaudeStreamOutput(
        string RawStdout,
        string Content,
        string? SessionId,
        string? ErrorMessage,
        bool SawJsonEvents);
}
