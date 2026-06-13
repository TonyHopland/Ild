using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Runs Anthropic's <c>claude</c> CLI in headless mode. Authentication is
/// expected to be set up out-of-band (e.g. <c>claude /login</c> for a Max
/// subscription, which writes credentials to <c>~/.claude</c>). The adapter
/// intentionally ignores <see cref="AiProvider.BaseUrl"/>,
/// <see cref="AiProvider.ApiKey"/> and <see cref="AiProvider.Model"/> — those
/// fields are not meaningful for subscription-based auth.
/// </summary>
public sealed class ClaudeCodeAdapter : CliAgentAdapterBase
{
    public ClaudeCodeAdapter()
    {
    }

    public ClaudeCodeAdapter(IServiceScopeFactory scopeFactory)
        : base(scopeFactory)
    {
    }

    public override string Name => "ClaudeCode";
    public override string[] SupportedProviderTypes => ["claude-code"];

    public override async Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
    {
        string? mcpConfigPath = null;
        try
        {
            var rendered = await RenderPromptAsync(ctx.Prompt, ctx.RunContext);
            var binaryPath = AiProviderConfig.Parse(ctx.Provider.Config).BinaryPathOr("claude");

            var worktreePath = ctx.RunContext.WorktreePath;
            if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath))
                return NodeExecutionResult.Fail(
                    "[claude-code-error] AI node requires a valid worktree path; refusing to run outside the loop's worktree.");

            // Inject the ILD MCP server entry so the headless `claude` process
            // can call list/create tools through the agent-scoped API. The CLI
            // accepts a JSON config via `--mcp-config <file>`, which we merge
            // with whatever the user has installed in their config — there is
            // no replace-only mode required here.
            mcpConfigPath = TryWriteIldMcpConfig(ctx.Provider, ctx.RunContext, ctx.ToolAllowlist);

            // Before invoking claude, if a managed session is in play and the
            // turn JSONL is missing from $HOME/.claude/projects, materialize
            // it from the snapshot store so `--resume` can pick up the
            // history even when the on-disk cache has been wiped.
            if (ctx.ManageSession && !string.IsNullOrWhiteSpace(ctx.SessionId))
                await TryRestoreSessionJsonlAsync(ctx, ctx.SessionId!, worktreePath);

            Process? proc;
            try
            {
                proc = Process.Start(BuildRunProcessStartInfo(binaryPath, worktreePath, rendered, ctx.SessionId, mcpConfigPath));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                return NodeExecutionResult.Fail(
                    $"[claude-code-error] cannot start '{binaryPath}' — make sure the claude CLI is installed and on PATH, and that you are logged in (e.g. via 'claude /login'). Details: {ex.Message}");
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
                KillProcessTree(process);
                return NodeExecutionResult.Fail("claude-code timed out");
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
                KillProcessTree(process);
                return NodeExecutionResult.Fail("claude-code stream read timed out");
            }

            var effectiveSessionId = stdout.SessionId ?? ctx.SessionId;
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

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout.ErrorMessage))
                return NodeExecutionResult.Fail($"claude-code session error: {stdout.ErrorMessage}", response);

            if (process.ExitCode == 0 && stdout.SawJsonEvents && string.IsNullOrWhiteSpace(stdout.Content))
                return NodeExecutionResult.Fail(response);

            // On a successful managed-session turn, copy the JSONL claude
            // wrote at $HOME/.claude/projects/<encoded-cwd>/<sid>.jsonl into
            // the snapshot store. This is what surfaces the run's session in
            // the "Available AI Sessions" panel and lets us rehydrate the
            // on-disk file on a later turn (see TryRestoreSessionJsonlAsync).
            if (process.ExitCode == 0 && ctx.ManageSession && !string.IsNullOrWhiteSpace(effectiveSessionId))
                await TryPersistSessionJsonlAsync(ctx, effectiveSessionId!, worktreePath);

            return process.ExitCode == 0
                ? NodeExecutionResult.Ok(response, rendered, effectiveSessionId, ctx.IncomingSessionId)
                : NodeExecutionResult.Fail($"exit={process.ExitCode} stderr={stderr}", response);
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

    public static ProcessStartInfo BuildRunProcessStartInfo(
        string binaryPath,
        string worktreePath,
        string renderedPrompt,
        string? sessionId,
        string? mcpConfigPath = null)
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
        psi.ArgumentList.Add("--add-dir");
        psi.ArgumentList.Add(worktreePath);
        psi.ArgumentList.Add("--permission-mode");
        psi.ArgumentList.Add("bypassPermissions");

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

        // Terminate option parsing before the prompt. `--mcp-config` is a
        // variadic option in the claude CLI: without this separator it greedily
        // consumes the trailing prompt as another config-file path, which the
        // CLI then fails to open (ENAMETOOLONG). The `--` guarantees the prompt
        // is treated as the positional prompt argument regardless of whether a
        // `--resume <id>` precedes it.
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(renderedPrompt);
        return psi;
    }

    /// <summary>
    /// Build the <c>mcpServers.ild</c> entry pointing at the ILD MCP server,
    /// in the shape Claude Code's <c>--mcp-config</c> file expects:
    /// <c>{ "command": "dotnet", "args": ["…/ild-mcp-server.dll"], "env": {…} }</c>.
    /// Returns <c>null</c> when no server DLL can be located, in which case
    /// the entry is omitted (failing open rather than poisoning the config).
    /// </summary>
    public static Dictionary<string, object?>? BuildIldMcpEntry(LoopRunContext? runContext)
    {
        var dllPath = IldMcpServer.ResolveServerDll();
        if (dllPath == null) return null;

        return new Dictionary<string, object?>
        {
            ["command"] = "dotnet",
            ["args"] = new[] { dllPath },
            ["env"] = IldMcpServer.BuildEnvironment(runContext),
        };
    }

    /// <summary>
    /// Serialize the ILD MCP config to a temp JSON file the caller can pass to
    /// <c>claude --mcp-config</c>. Returns <c>null</c> when the <c>ild</c>
    /// tool isn't in the allowlist, when no server DLL can be located, or when
    /// the temp file can't be written.
    /// </summary>
    public static string? TryWriteIldMcpConfig(AiProvider provider, LoopRunContext runContext, IReadOnlyList<string>? toolAllowlist)
    {
        var enabledKeys = AiToolCatalog.NormalizeSelectedToolKeys(provider.Type, toolAllowlist);
        if (!enabledKeys.Contains(AiToolCatalog.Ild, StringComparer.OrdinalIgnoreCase))
            return null;

        var entry = BuildIldMcpEntry(runContext);
        if (entry == null) return null;

        var config = new Dictionary<string, object?>
        {
            ["mcpServers"] = new Dictionary<string, object?>
            {
                ["ild"] = entry,
            },
        };

        var json = JsonSerializer.Serialize(config);
        var path = Path.Combine(Path.GetTempPath(), $"ild-claude-mcp-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            return path;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static async Task<ClaudeStreamOutput> ReadStreamJsonAsync(
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
    /// message (e.g. <c>\n[tool: Bash]\n</c>). These feed the live view so the
    /// run's tool activity is visible, without polluting the node's text output.
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

            var name = TryGetString(item, "name", out var toolName) && !string.IsNullOrWhiteSpace(toolName)
                ? toolName
                : "tool";
            yield return $"\n[tool: {name}]\n";
        }
    }

    private async Task TryRestoreSessionJsonlAsync(AgentExecutionContext ctx, string sessionId, string worktreePath)
    {
        if (ScopeFactory is null) return;

        var path = GetSessionFilePath(worktreePath, sessionId);
        if (path is null || File.Exists(path)) return;

        AdapterSessionSnapshot? snapshot;
        try
        {
            snapshot = await GetSnapshotAsync(ctx.RunContext.LoopRunId, sessionId, ctx.Cancel);
        }
        catch
        {
            return;
        }

        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.SessionJson)) return;

        var jsonl = UnwrapJsonl(snapshot.SessionJson);
        if (jsonl is null) return;

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, jsonl, ctx.Cancel);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task TryPersistSessionJsonlAsync(AgentExecutionContext ctx, string sessionId, string worktreePath)
    {
        if (ScopeFactory is null) return;

        var path = GetSessionFilePath(worktreePath, sessionId);
        if (path is null || !File.Exists(path)) return;

        string jsonl;
        try
        {
            jsonl = await File.ReadAllTextAsync(path, ctx.Cancel);
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        var wrapped = WrapJsonl(sessionId, jsonl);

        try
        {
            await UpsertSnapshotAsync(ctx.RunContext.LoopRunId, sessionId, wrapped, ctx.Cancel);
        }
        catch
        {
            // Snapshot persistence is observational — never fail a run because
            // the UI-facing snapshot couldn't be written.
        }
    }

    public static string? GetSessionFilePath(string worktreePath, string sessionId)
    {
        if (string.IsNullOrEmpty(worktreePath) || string.IsNullOrEmpty(sessionId)) return null;
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;
        return Path.Combine(home, ".claude", "projects", EncodeWorktreePath(worktreePath), $"{sessionId}.jsonl");
    }

    // Claude Code stores per-cwd session JSONL files under
    // ~/.claude/projects/<encoded-cwd>/. The encoding maps both path separators
    // and dots to dashes (e.g. /home/ild/wi-1.2 -> -home-ild-wi-1-2). Mapping
    // only '/' left dotted worktree paths pointing at the wrong directory, so
    // snapshot persist/restore silently no-op'd. If Anthropic ever changes the
    // rule, restore/persist still no-op (path miss) rather than misbehave.
    public static string EncodeWorktreePath(string path) => path.Replace('/', '-').Replace('.', '-');

    public static string WrapJsonl(string sessionId, string jsonl)
    {
        var events = new JsonArray();
        using var reader = new StringReader(jsonl);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;
            try
            {
                var node = JsonNode.Parse(line);
                if (node is not null) events.Add(node);
            }
            catch (JsonException)
            {
                // Skip malformed lines; the wrapper is a UI-facing summary,
                // not a faithful copy.
            }
        }

        var obj = new JsonObject
        {
            ["format"] = "claude-jsonl",
            ["sessionId"] = sessionId,
            ["events"] = events,
        };
        return obj.ToJsonString();
    }

    public static string? UnwrapJsonl(string sessionJson)
    {
        if (string.IsNullOrWhiteSpace(sessionJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(sessionJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("events", out var events)
                || events.ValueKind != JsonValueKind.Array) return null;

            var sb = new StringBuilder();
            foreach (var evt in events.EnumerateArray())
                sb.AppendLine(evt.GetRawText());
            return sb.ToString();
        }
        catch (JsonException) { return null; }
    }

    private sealed record ClaudeStreamOutput(
        string RawStdout,
        string Content,
        string? SessionId,
        string? ErrorMessage,
        bool SawJsonEvents);
}
