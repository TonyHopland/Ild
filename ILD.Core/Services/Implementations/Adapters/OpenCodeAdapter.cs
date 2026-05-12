using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Adapters;

public class OpenCodeAdapter : IAgentAdapter
{
    private static readonly IPromptTemplateResolver Resolver = new PromptTemplateResolver();
    private readonly IServiceScopeFactory? _scopeFactory;

    public OpenCodeAdapter()
    {
    }

    public OpenCodeAdapter(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string Name => "OpenCode";
    public string[] SupportedProviderTypes => ["opencode"];
    public ConfigFieldDescriptor[] ConfigSchema => Array.Empty<ConfigFieldDescriptor>();

    public async Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
    {
        try
        {
            var prompt = ctx.ExecutionCount == 1 ? ctx.InitialPrompt : ctx.LoopPrompt;
            var rendered = await RenderPromptAsync(prompt, ctx.RunContext);

            var binaryPath = ResolveBinaryPath(ctx.Provider);

            if (string.IsNullOrEmpty(binaryPath))
                return NodeExecutionResult.Fail("[opencode-error] binaryPath is not configured");

            var worktreePath = ctx.RunContext.WorktreePath;
            if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath))
                return NodeExecutionResult.Fail(
                    "[opencode-error] AI node requires a valid worktree path; refusing to run outside the loop's worktree.");

            if (!string.IsNullOrEmpty(ctx.SessionId))
            {
                var restoreError = await RestoreManagedSessionAsync(binaryPath, worktreePath, ctx);
                if (!string.IsNullOrEmpty(restoreError))
                    return NodeExecutionResult.Fail(restoreError);
            }

            var psi = BuildProcessStartInfo(binaryPath, worktreePath);

            var (opencodeModel, opencodeConfigJson) = BuildOpenCodeConfig(ctx.Provider, ctx.RunContext);

            psi.EnvironmentVariables["OPENCODE_CONFIG_CONTENT"] = opencodeConfigJson;

            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--dir");
            psi.ArgumentList.Add(worktreePath);
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(opencodeModel);
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("json");
            if (!string.IsNullOrEmpty(ctx.SessionId))
            {
                psi.ArgumentList.Add("--session");
                psi.ArgumentList.Add(ctx.SessionId);
            }
            psi.ArgumentList.Add("--dangerously-skip-permissions");
            psi.ArgumentList.Add(rendered);

            Process? proc = null;
            try
            {
                proc = Process.Start(psi);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                if (ShouldRetryWithoutWorkingDirectory(ex, worktreePath))
                {
                    try
                    {
                        proc = Process.Start(BuildProcessStartInfo(binaryPath, worktreePath, useWorktreeAsWorkingDirectory: false));
                    }
                    catch (Exception retryEx) when (retryEx is InvalidOperationException or IOException)
                    {
                        return NodeExecutionResult.Fail($"[opencode-error] cannot start '{binaryPath}' — make sure the opencode binary is installed and on PATH. Details: {retryEx.Message}");
                    }
                }
                else
                {
                    return NodeExecutionResult.Fail($"[opencode-error] cannot start '{binaryPath}' — make sure the opencode binary is installed and on PATH. Details: {ex.Message}");
                }
            }
            using var p = proc ?? throw new InvalidOperationException("Process.Start returned null");

            var stdoutLines = new List<string>();
            var stderrLines = new List<string>();
            var stdoutLock = new object();
            var stderrLock = new object();

            var stdoutTask = ReadAndStreamLinesAsync(p.StandardOutput, stdoutLines, stdoutLock, ctx.ProgressCallback, ctx.Cancel);
            var stderrTask = ReadAndStreamLinesAsync(p.StandardError, stderrLines, stderrLock, ctx.ProgressCallback, ctx.Cancel);

            try
            {
                await p.WaitForExitAsync(ctx.Cancel);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return NodeExecutionResult.Fail("opencode timed out");
            }

            string stdout, stderr;
            try
            {
                stdout = await stdoutTask;
                stderr = await stderrTask;
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return NodeExecutionResult.Fail("opencode stream read timed out");
            }
            var (response, sessionId, jsonError, sawJsonEvents) = ExtractTextAndSessionIdFromJsonEvents(stdout);

            // opencode --format json emits a stream of typed events (step_start,
            // tool_use, text, error, ...). Only `text` events carry assistant
            // output, and `error` events surface session failures. Previously
            // we fell back to dumping the raw NDJSON whenever no text was
            // extracted, which made the AI node output unreadable when the
            // model only produced tool calls or the session errored. Surface
            // a clear diagnostic instead and propagate session errors. When
            // stdout is not JSON at all (e.g. tests using a stub binary), keep
            // the raw stdout as the response.
            if (string.IsNullOrEmpty(response))
            {
                if (!string.IsNullOrEmpty(jsonError))
                    response = $"[opencode-error] {jsonError}";
                else if (!sawJsonEvents)
                    response = stdout;
                else if (!string.IsNullOrEmpty(stderr))
                    response = $"[opencode] no text response. stderr: {stderr.Trim()}";
                else
                    response = "[opencode] no text response from model";
            }

            if (p.ExitCode == 0 && !string.IsNullOrEmpty(jsonError))
                return NodeExecutionResult.Fail($"opencode session error: {jsonError}", response);

            var effectiveSessionId = sessionId ?? ctx.SessionId;
            if (p.ExitCode == 0 && !string.IsNullOrEmpty(effectiveSessionId))
            {
                var exportError = await PersistManagedSessionAsync(binaryPath, worktreePath, ctx, effectiveSessionId);
                if (!string.IsNullOrEmpty(exportError))
                    return NodeExecutionResult.Fail(exportError, response);
            }

            return p.ExitCode == 0
                ? NodeExecutionResult.Ok(response, rendered, effectiveSessionId, ctx.IncomingSessionId)
                : NodeExecutionResult.Fail($"exit={p.ExitCode} stderr={stderr}", response);
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Fail($"[opencode-error] {ex.Message}");
        }
    }

    private async Task<string?> RestoreManagedSessionAsync(string binaryPath, string worktreePath, AgentExecutionContext ctx)
    {
        if (_scopeFactory is null || string.IsNullOrEmpty(ctx.SessionId))
            return null;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var snapshotStore = scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>();
        var snapshot = await snapshotStore.GetAsync(ctx.RunContext.LoopRunId, Name, ctx.SessionId, ctx.Cancel);
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.SessionJson))
            return null;

        var localSession = await RunOpencodeCommandAsync(binaryPath, worktreePath, ctx.Cancel, ["export", ctx.SessionId]);
        if (localSession.ExitCode == 0 && !string.IsNullOrWhiteSpace(localSession.Stdout))
            return null;

        var tempFile = Path.Combine(Path.GetTempPath(), $"ild-opencode-session-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempFile, snapshot.SessionJson, ctx.Cancel);

        try
        {
            var importResult = await RunOpencodeCommandAsync(binaryPath, worktreePath, ctx.Cancel, ["import", tempFile]);
            if (importResult.ExitCode != 0)
                return $"[opencode-error] failed to import managed session '{ctx.SessionId}': {BuildCommandFailure(importResult)}";
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }

        return null;
    }

    private async Task<string?> PersistManagedSessionAsync(string binaryPath, string worktreePath, AgentExecutionContext ctx, string sessionId)
    {
        if (_scopeFactory is null)
            return null;

        var exportResult = await RunOpencodeCommandAsync(binaryPath, worktreePath, ctx.Cancel, ["export", sessionId]);
        if (exportResult.ExitCode != 0)
            return $"[opencode-error] failed to export managed session '{sessionId}': {BuildCommandFailure(exportResult)}";
        if (string.IsNullOrWhiteSpace(exportResult.Stdout))
            return $"[opencode-error] failed to export managed session '{sessionId}': no session json returned";

        await using var scope = _scopeFactory.CreateAsyncScope();
        var snapshotStore = scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>();
        await snapshotStore.UpsertAsync(ctx.RunContext.LoopRunId, Name, sessionId, exportResult.Stdout.Trim(), ctx.Cancel);
        return null;
    }

    private static async Task<OpencodeCommandResult> RunOpencodeCommandAsync(
        string binaryPath,
        string worktreePath,
        CancellationToken cancellationToken,
        IReadOnlyList<string> arguments)
    {
        Process? proc = null;
        try
        {
            var psi = BuildProcessStartInfo(binaryPath, worktreePath);
            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            proc = Process.Start(psi);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            if (ShouldRetryWithoutWorkingDirectory(ex, worktreePath))
            {
                var retryPsi = BuildProcessStartInfo(binaryPath, worktreePath, useWorktreeAsWorkingDirectory: false);
                foreach (var argument in arguments)
                    retryPsi.ArgumentList.Add(argument);
                proc = Process.Start(retryPsi);
            }
            else
            {
                throw;
            }
        }

        using var process = proc ?? throw new InvalidOperationException("Process.Start returned null");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return new OpencodeCommandResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string BuildCommandFailure(OpencodeCommandResult result)
    {
        var stderr = result.Stderr.Trim();
        var stdout = result.Stdout.Trim();
        if (!string.IsNullOrEmpty(stderr))
            return $"exit={result.ExitCode} stderr={stderr}";
        if (!string.IsNullOrEmpty(stdout))
            return $"exit={result.ExitCode} stdout={stdout}";
        return $"exit={result.ExitCode}";
    }

    private sealed record OpencodeCommandResult(int ExitCode, string Stdout, string Stderr);

    private static ProcessStartInfo BuildProcessStartInfo(string binaryPath, string worktreePath, bool useWorktreeAsWorkingDirectory = true)
    {
        var psi = new ProcessStartInfo(binaryPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (useWorktreeAsWorkingDirectory)
            psi.WorkingDirectory = worktreePath;

        return psi;
    }

    private static bool ShouldRetryWithoutWorkingDirectory(Exception ex, string worktreePath)
    {
        if (!Directory.Exists(worktreePath))
            return false;

        var message = ex.Message;
        return message.Contains("working directory", StringComparison.OrdinalIgnoreCase)
            || message.Contains(worktreePath, StringComparison.Ordinal);
    }

    static string ExtractTextFromJsonEvents(string raw)
    {
        var sb = new StringBuilder();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                ExtractTextFromElement(doc.RootElement, sb);
            }
            catch { }
        }
        return sb.ToString();
    }

    static (string Text, string? SessionId, string? Error, bool SawJsonEvents) ExtractTextAndSessionIdFromJsonEvents(string raw)
    {
        var sb = new StringBuilder();
        string? lastSessionId = null;
        string? lastError = null;
        var sawJsonEvents = false;
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                // Recognise opencode `--format json` events by their `type`
                // discriminator. Plain JSON output (e.g. dumped env vars in
                // tests) doesn't have one and should fall through to the
                // raw-stdout fallback below.
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("type", out var typeProbe)
                    && typeProbe.ValueKind == JsonValueKind.String)
                {
                    sawJsonEvents = true;
                }
                ExtractTextFromElement(doc.RootElement, sb);
                // opencode's JSON output may carry the session id at the
                // root (`{"sessionId": "..."}`) or nested under a `session`
                // object (`{"session":{"id":"..."}}`). Walk the tree so we
                // pick it up regardless of the event shape.
                var found = ExtractSessionIdFromElement(doc.RootElement);
                if (!string.IsNullOrEmpty(found)) lastSessionId = found;

                // `--format json` emits typed events; capture session errors
                // so the caller can surface them instead of silently producing
                // an empty response.
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("type", out var typeProp)
                    && typeProp.ValueKind == JsonValueKind.String
                    && typeProp.GetString() == "error"
                    && doc.RootElement.TryGetProperty("error", out var errProp))
                {
                    var msg = ExtractErrorMessage(errProp);
                    if (!string.IsNullOrEmpty(msg)) lastError = msg;
                }
            }
            catch { }
        }
        return (sb.ToString(), lastSessionId, lastError, sawJsonEvents);
    }

    static string? ExtractErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
            return error.GetString();
        if (error.ValueKind != JsonValueKind.Object)
            return null;
        if (error.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("message", out var dataMsg)
            && dataMsg.ValueKind == JsonValueKind.String)
            return dataMsg.GetString();
        if (error.TryGetProperty("message", out var msg)
            && msg.ValueKind == JsonValueKind.String)
            return msg.GetString();
        if (error.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String)
            return name.GetString();
        return null;
    }

    static string? ExtractSessionIdFromElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, "sessionId", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.String)
                {
                    var v = prop.Value.GetString();
                    if (!string.IsNullOrEmpty(v)) return v;
                }
                if (string.Equals(prop.Name, "session", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.Object
                    && prop.Value.TryGetProperty("id", out var sid)
                    && sid.ValueKind == JsonValueKind.String)
                {
                    var v = sid.GetString();
                    if (!string.IsNullOrEmpty(v)) return v;
                }
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var nested = ExtractSessionIdFromElement(prop.Value);
                    if (!string.IsNullOrEmpty(nested)) return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = ExtractSessionIdFromElement(item);
                if (!string.IsNullOrEmpty(nested)) return nested;
            }
        }
        return null;
    }

    static void ExtractTextFromElement(JsonElement element, StringBuilder sb)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var name = prop.Name.ToLowerInvariant();
                if (name is "text" or "content" or "delta" or "message" or "body")
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var val = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(val))
                            sb.Append(val);
                    }
                    else
                        ExtractTextFromElement(prop.Value, sb);
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array)
                {
                    ExtractTextFromElement(prop.Value, sb);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ExtractTextFromElement(item, sb);
        }
    }

    private static string ResolveBinaryPath(AiProvider provider)
    {
        var binaryPath = "opencode";

        if (!string.IsNullOrEmpty(provider.Config))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(provider.Config);
                var root = doc.RootElement;
                if (root.TryGetProperty("binaryPath", out var bp) && bp.ValueKind == System.Text.Json.JsonValueKind.String)
                    binaryPath = bp.GetString() ?? binaryPath;
            }
            catch { }
        }

        return binaryPath;
    }

    private static (string ModelRef, string ConfigJson) BuildOpenCodeConfig(AiProvider provider, LoopRunContext? runContext = null)
    {
        var providerId = SanitizeProviderId(provider.Name);
        var modelId = provider.Model;

        var baseUrl = provider.BaseUrl.TrimEnd('/');

        var config = new Dictionary<string, object?>
        {
            ["provider"] = new Dictionary<string, object?>
            {
                [providerId] = new Dictionary<string, object?>
                {
                    ["npm"] = "@ai-sdk/openai-compatible",
                    ["name"] = provider.Name,
                    ["options"] = new Dictionary<string, object?>
                    {
                        ["baseURL"] = baseUrl,
                        ["apiKey"] = provider.ApiKey ?? string.Empty,
                    },
                    ["models"] = new Dictionary<string, object?>
                    {
                        [modelId] = new Dictionary<string, object?> { ["name"] = modelId },
                    },
                },
            },
        };

        // Inject the ILD MCP server entry so agents can list/create work items
        // through the agent-scoped API. The opencode child process inherits its
        // *own* config via OPENCODE_CONFIG_CONTENT, which means the user's
        // ~/.config/opencode/opencode.json (and any mcp entries it contains) is
        // ignored — we have to add the entry here ourselves.
        var ildMcp = BuildIldMcpEntry(runContext);
        if (ildMcp != null)
        {
            config["mcp"] = new Dictionary<string, object?>
            {
                ["ild"] = ildMcp,
            };
        }

        var configJson = JsonSerializer.Serialize(config);
        var modelRef = $"{providerId}/{modelId}";
        return (modelRef, configJson);
    }

    /// <summary>
    /// Build the opencode <c>mcp.ild</c> entry pointing at the ILD MCP server.
    /// Returns <c>null</c> when no server DLL can be located, in which case
    /// the entry is omitted (failing open rather than poisoning the config).
    /// </summary>
    public static Dictionary<string, object?>? BuildIldMcpEntry(LoopRunContext? runContext)
    {
        var dllPath = ResolveIldMcpServerDll();
        if (dllPath == null) return null;

        var apiUrl = Environment.GetEnvironmentVariable("ILD_API_URL")
            ?? "http://localhost:5000";

        var environment = new Dictionary<string, object?>
        {
            ["ILD_API_URL"] = apiUrl,
        };

        var apiToken = Environment.GetEnvironmentVariable("ILD_API_TOKEN");
        if (!string.IsNullOrEmpty(apiToken))
            environment["ILD_API_TOKEN"] = apiToken;

        if (runContext != null)
            environment["ILD_LOOP_RUN_ID"] = runContext.LoopRunId.ToString();

        return new Dictionary<string, object?>
        {
            ["type"] = "local",
            ["command"] = new[] { "dotnet", dllPath },
            ["environment"] = environment,
        };
    }

    /// <summary>
    /// Locate the published <c>ild-mcp-server.dll</c>. Probes in order:
    ///   1. <c>ILD_MCP_SERVER_DLL</c> env var (explicit override),
    ///   2. next to the currently executing assembly,
    ///   3. walk up from the executing assembly looking for a sibling
    ///      <c>ILD.McpServer/bin/{Debug|Release}/net*</c> directory (dev case).
    /// Returns <c>null</c> if nothing is found.
    /// </summary>
    public static string? ResolveIldMcpServerDll()
    {
        var envOverride = Environment.GetEnvironmentVariable("ILD_MCP_SERVER_DLL");
        if (!string.IsNullOrEmpty(envOverride) && File.Exists(envOverride))
            return Path.GetFullPath(envOverride);

        const string DllName = "ild-mcp-server.dll";

        var baseDir = AppContext.BaseDirectory;
        var sibling = Path.Combine(baseDir, DllName);
        if (File.Exists(sibling)) return Path.GetFullPath(sibling);

        // Walk upwards (max 8 levels) looking for an ILD.McpServer build output.
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidateRoot = Path.Combine(dir.FullName, "ILD.McpServer", "bin");
            if (!Directory.Exists(candidateRoot)) continue;

            // Prefer Release over Debug if both exist.
            foreach (var flavor in new[] { "Release", "Debug" })
            {
                var flavorDir = Path.Combine(candidateRoot, flavor);
                if (!Directory.Exists(flavorDir)) continue;
                var hit = Directory.GetFiles(flavorDir, DllName, SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (hit != null) return hit;
            }
        }

        return null;
    }

    private static string SanitizeProviderId(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                sb.Append(c);
            else if (c == ' ' || c == '.')
                sb.Append('-');
        }
        return sb.ToString() ?? "provider";
    }

    private static Task<string> RenderPromptAsync(string template, LoopRunContext context)
        => Task.FromResult(Resolver.Render(template, new PromptContext(
            WorkItemTitle: context.WorkItemTitle,
            WorkItemDescription: context.WorkItemDescription,
            PreviousNodeOutput: context.PreviousNodeOutput,
            EventLogSummary: context.EventLogSummary,
            WorktreePath: context.WorktreePath)));

    private static async Task<string> ReadAndStreamLinesAsync(
        System.IO.StreamReader reader,
        List<string> lines,
        object lockObj,
        Func<string, Task>? progressCallback,
        CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            lock (lockObj) lines.Add(line);
            if (progressCallback != null)
            {
                var text = ExtractTextFromJsonLine(line);
                if (!string.IsNullOrEmpty(text))
                    await progressCallback(text).ConfigureAwait(false);
            }
        }
        lock (lockObj) return string.Join("\n", lines);
    }

    private static string? ExtractTextFromJsonLine(string line)
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var sb = new StringBuilder();
            ExtractTextFromElement(doc.RootElement, sb);
            if (sb.Length > 0) return sb.ToString();

            // No text content; surface event metadata based on real opencode output
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var type = TryGetString(doc.RootElement, "type");
                if (type == "tool_use")
                {
                    var tool = TryGetString(doc.RootElement, "tool");
                    return tool != null ? $"[tool: {tool}]" : "[tool call]";
                }
                if (type != null)
                {
                    return $"[{type}]";
                }
            }
            return null;
        }
        catch
        {
            // Not valid JSON; pass through as-is for non-JSON output
            return trimmed;
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }
}
