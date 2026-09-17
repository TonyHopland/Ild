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

public sealed class PiAdapter : CliAgentAdapterBase
{
    public PiAdapter()
    {
    }

    public PiAdapter(IServiceScopeFactory scopeFactory)
        : base(scopeFactory)
    {
    }

    public override string Name => "Pi";
    public override string[] SupportedProviderTypes => ["pi"];

    public override async Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
    {
        try
        {
            var settings = ResolveSettings(ctx.Provider, ctx.RunContext, ctx.ToolAllowlist, ctx.ChatSessionId);

            if (string.IsNullOrWhiteSpace(settings.BinaryPath))
                return NodeExecutionResult.Fail("[pi-error] binaryPath is not configured");

            var worktreePath = ctx.RunContext.WorktreePath;
            if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
                return NodeExecutionResult.Fail(
                    "[pi-error] AI node requires a valid worktree path; refusing to run outside the loop's worktree.");

            // ADR-0011 parity note: claude/opencode sandbox file tools to their
            // working directory and need an explicit grant (claude `--add-dir`,
            // opencode `external_directory`) to reach an extra path like the Chat
            // Context's open-work-item worktree. Pi's file tools take absolute
            // paths and are not directory-sandboxed, so the path supplied in the
            // turn's Chat Context preamble is already reachable — there is no
            // per-directory config to set for ctx.AdditionalAllowedDirectories.

            var sessionDirectory = Path.Combine(AgentIsolation.ScratchRoot, SessionDirSegment, ctx.RunContext.LoopRunId.ToString("N"));
            await AgentWritableFiles.CreateDirectoryAsync(sessionDirectory, ctx.Cancel);
            await PrepareRuntimeFilesAsync(settings, ctx.Cancel);

            string? sessionIdToUse = ctx.SessionId;
            string? sessionPathToUse = null;
            // Fork: seed a copy of the source session under the destination id
            // before restore, so the restore below rehydrates the copy and pi
            // continues on the fork while the source stays frozen.
            if (ctx.ManageSession && !string.IsNullOrWhiteSpace(sessionIdToUse) && !string.IsNullOrWhiteSpace(ctx.ForkFromSessionId))
                await ForkSessionSnapshotAsync(ctx.RunContext.LoopRunId, ctx.ForkFromSessionId!, sessionIdToUse!, ctx.Cancel);
            if (ctx.ManageSession && !string.IsNullOrWhiteSpace(sessionIdToUse))
            {
                var restoreResult = await RestoreManagedSessionAsync(sessionDirectory, ctx, sessionIdToUse);

                sessionIdToUse = restoreResult.SessionIdToUse;
                sessionPathToUse = restoreResult.SessionPathToUse;
            }

            Process? proc;
            try
            {
                proc = StartAgentProcess(BuildRunProcessStartInfo(
                    settings,
                    worktreePath,
                    sessionDirectory,
                    sessionIdToUse,
                    sessionPathToUse), ctx.Provider.Id);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                return NodeExecutionResult.Fail($"[pi-error] cannot start '{settings.BinaryPath}' — install or update Pi from the AI Provider page, or make sure the pi binary is on PATH. Details: {ex.Message}");
            }

            using var process = proc ?? throw new InvalidOperationException("Process.Start returned null");
            try
            {
                await process.StandardInput.WriteAsync(ctx.Prompt.AsMemory(), ctx.Cancel);
                await process.StandardInput.FlushAsync(ctx.Cancel);
            }
            catch (IOException)
            {
                // Child already closed stdin (e.g. consumed enough of the
                // prompt and exited). Don't discard its stdout — let the
                // exit code and parsed output decide success.
            }
            try { process.StandardInput.Close(); } catch (IOException) { }
            var stdoutTask = ReadStdoutAsync(process.StandardOutput, ctx.ProgressCallback, ctx.OnSessionId, ctx.Cancel);
            var stderrTask = process.StandardError.ReadToEndAsync(ctx.Cancel);

            try
            {
                await process.WaitForExitAsync(ctx.Cancel);
            }
            catch (OperationCanceledException)
            {
                return NodeExecutionResult.Fail(KillAndDescribe(process, "pi timed out"));
            }

            PiExecutionOutput stdout;
            string stderr;
            try
            {
                stdout = await stdoutTask;
                stderr = await stderrTask;
            }
            catch (OperationCanceledException)
            {
                return NodeExecutionResult.Fail(KillAndDescribe(process, "pi stream read timed out"));
            }

            var effectiveSessionId = stdout.SessionId ?? sessionIdToUse;
            if (ctx.ManageSession && process.ExitCode == 0 && !string.IsNullOrWhiteSpace(effectiveSessionId))
                await PersistManagedSessionAsync(sessionDirectory, effectiveSessionId!, ctx);

            var response = stdout.Content;
            if (string.IsNullOrWhiteSpace(response))
            {
                if (!stdout.SawJsonEvents)
                    response = stdout.RawStdout;
                else if (!string.IsNullOrWhiteSpace(stderr))
                    response = $"[pi] no assistant text response. stderr: {stderr.Trim()}";
                else
                    response = "[pi] no assistant text response from model";
            }

            // Pi sometimes exits 0 mid-turn (e.g. provider closed the stream
            // early) leaving the assistant message truncated. Without a
            // message_end/turn_end marker we cannot trust the partial text as
            // the final response; surface it as a retryable failure rather
            // than letting downstream nodes consume half a sentence.
            if (process.ExitCode == 0 && stdout.SawJsonEvents && !stdout.SawTurnEnd)
                return NodeExecutionResult.Fail(
                    "pi stream ended before message_end/turn_end (assistant turn was truncated)",
                    response);

            // Turn completed cleanly but model produced no text — e.g. the
            // provider silently dropped the request. Treat as retryable failure
            // so the on_failure edge can recover rather than propagating the
            // error string as real AI output.
            if (process.ExitCode == 0 && stdout.SawJsonEvents && string.IsNullOrWhiteSpace(stdout.Content))
                return NodeExecutionResult.Fail(response);

            return process.ExitCode == 0
                ? NodeExecutionResult.Ok(response, ctx.Prompt, effectiveSessionId, ctx.IncomingSessionId, AdapterUsageParser.Parse(stdout.RawStdout))
                : NodeExecutionResult.Fail($"exit={process.ExitCode} stderr={stderr}", response);
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Fail($"[pi-error] {ex.Message}");
        }
    }

    // The prompt is not an argument here — pi reads its turn from stdin (see
    // the StandardInput write in ExecuteAsync).
    internal static ProcessStartInfo BuildRunProcessStartInfo(
        PiAdapterSettings settings,
        string worktreePath,
        string sessionDirectory,
        string? sessionId,
        string? sessionPath)
    {
        var psi = new ProcessStartInfo(settings.BinaryPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = worktreePath,
        };

        psi.EnvironmentVariables["PI_SKIP_VERSION_CHECK"] = "1";
        psi.EnvironmentVariables["PI_TELEMETRY"] = "0";
        psi.EnvironmentVariables["PI_CODING_AGENT_SESSION_DIR"] = sessionDirectory;

        if (!string.IsNullOrWhiteSpace(settings.AgentDirectory))
            psi.EnvironmentVariables["PI_CODING_AGENT_DIR"] = settings.AgentDirectory;

        if (!string.IsNullOrWhiteSpace(settings.ApiKeyEnvironmentVariableName)
            && !string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            psi.EnvironmentVariables[settings.ApiKeyEnvironmentVariableName] = settings.ApiKey;
        }

        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--session-dir");
        psi.ArgumentList.Add(sessionDirectory);

        if (settings.ToolNames.Count > 0)
        {
            psi.ArgumentList.Add("--tools");
            psi.ArgumentList.Add(string.Join(',', settings.ToolNames));
        }

        if (!string.IsNullOrWhiteSpace(settings.IldExtensionPath))
        {
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(settings.IldExtensionPath);
        }

        if (!string.IsNullOrWhiteSpace(settings.Provider))
        {
            psi.ArgumentList.Add("--provider");
            psi.ArgumentList.Add(settings.Provider);
        }

        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(settings.Model);
        }

        if (settings.PassApiKeyViaCli && !string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            psi.ArgumentList.Add("--api-key");
            psi.ArgumentList.Add(settings.ApiKey);
        }

        if (!string.IsNullOrWhiteSpace(sessionPath))
        {
            psi.ArgumentList.Add("--session");
            psi.ArgumentList.Add(sessionPath);
        }
        else if (!string.IsNullOrWhiteSpace(sessionId))
        {
            psi.ArgumentList.Add("--session");
            psi.ArgumentList.Add(sessionId);
        }

        return psi;
    }

    private async Task<ManagedSessionRestoreResult> RestoreManagedSessionAsync(string sessionDirectory, AgentExecutionContext ctx, string sessionId)
    {
        var localSessionPath = await FindSessionFileAsync(sessionDirectory, sessionId, ctx.Cancel);
        if (localSessionPath is not null)
            return ManagedSessionRestoreResult.Use(sessionId, localSessionPath);

        if (ScopeFactory is null)
            return ManagedSessionRestoreResult.StartFresh();

        var snapshot = await GetSnapshotAsync(ctx, sessionId, ctx.Cancel);
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.SessionJson))
            return ManagedSessionRestoreResult.StartFresh();

        var restoredPath = BuildSnapshotPath(sessionDirectory, sessionId);
        await AgentWritableFiles.WriteFileAsync(restoredPath, snapshot.SessionJson, ctx.Cancel);
        return ManagedSessionRestoreResult.Use(sessionId, restoredPath);
    }

    private async Task PersistManagedSessionAsync(string sessionDirectory, string sessionId, AgentExecutionContext ctx)
    {
        if (ScopeFactory is null)
            return;

        var sessionPath = await FindSessionFileAsync(sessionDirectory, sessionId, ctx.Cancel);
        if (sessionPath is null)
            return;

        var sessionJson = await AgentWritableFiles.ReadFileAsync(sessionPath, ctx.Cancel);
        if (sessionJson is null)
            return;

        await UpsertSnapshotAsync(ctx, sessionId, sessionJson, ctx.Cancel);
    }

    private static async Task<PiExecutionOutput> ReadStdoutAsync(StreamReader reader, Func<string, Task>? progressCallback, Action<string>? onSessionId, CancellationToken ct)
    {
        var raw = new StringBuilder();
        var content = new StringBuilder();
        string? sessionId = null;
        string? completedAssistantText = null;
        var sawJsonEvents = false;
        var sawTurnEnd = false;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            raw.AppendLine(line);

            if (!TryParseJson(line, out var doc))
                continue;

            sawJsonEvents = true;
            using var jsonDoc = doc!;
            {
                var root = jsonDoc.RootElement;
                var hasEventType = TryGetString(root, "type", out var eventType);
                if (hasEventType && string.Equals(eventType, "session", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryGetString(root, "id", out var headerSessionId))
                    {
                        var isFirst = sessionId is null;
                        sessionId = headerSessionId;
                        if (isFirst) FireSessionId(onSessionId, headerSessionId);
                    }
                    continue;
                }

                if (hasEventType
                    && string.Equals(eventType, "message_update", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("assistantMessageEvent", out var assistantEvent)
                    && TryGetString(assistantEvent, "type", out var assistantEventType)
                    && string.Equals(assistantEventType, "text_delta", StringComparison.OrdinalIgnoreCase)
                    && TryGetString(assistantEvent, "delta", out var delta)
                    && !string.IsNullOrEmpty(delta))
                {
                    content.Append(delta);
                    if (progressCallback is not null)
                        await progressCallback(delta).ConfigureAwait(false);
                    continue;
                }

                // `pi --mode json` announces each tool call as its own event
                // carrying the tool's name and arguments. It belongs on the live
                // stream only — the node's output is the assistant's text.
                if (hasEventType
                    && string.Equals(eventType, "tool_execution_start", StringComparison.OrdinalIgnoreCase))
                {
                    if (progressCallback is not null)
                    {
                        var arguments = root.TryGetProperty("args", out var args) ? args : default;
                        var marker = ToolMarkerFormatter.Format(GetString(root, "toolName"), arguments);
                        await progressCallback($"\n{marker}\n").ConfigureAwait(false);
                    }
                    continue;
                }

                if (hasEventType
                    && (string.Equals(eventType, "message_end", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(eventType, "turn_end", StringComparison.OrdinalIgnoreCase)))
                {
                    sawTurnEnd = true;
                    if (root.TryGetProperty("message", out var message) && IsAssistantMessage(message))
                    {
                        var assistantText = ExtractAssistantText(message);
                        if (!string.IsNullOrWhiteSpace(assistantText))
                            completedAssistantText = assistantText;
                    }
                }
            }
        }

        var finalContent = content.Length > 0
            ? content.ToString()
            : completedAssistantText ?? string.Empty;

        return new PiExecutionOutput(raw.ToString(), finalContent, sessionId, sawJsonEvents, sawTurnEnd);
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
            if (item.ValueKind == JsonValueKind.String)
            {
                sb.Append(item.GetString());
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (TryGetString(item, "text", out var directText) && !string.IsNullOrWhiteSpace(directText))
            {
                sb.Append(directText);
                continue;
            }

            if (item.TryGetProperty("text", out var nestedText) && nestedText.ValueKind == JsonValueKind.Object
                && TryGetString(nestedText, "value", out var textValue) && !string.IsNullOrWhiteSpace(textValue))
            {
                sb.Append(textValue);
            }
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static bool IsAssistantMessage(JsonElement message)
        => TryGetString(message, "role", out var role)
            && string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseJson(string line, out JsonDocument? doc)
    {
        try
        {
            doc = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            doc = null;
            return false;
        }
    }

    private const string SessionDirSegment = "ild-pi-sessions";
    private const string AgentDirSegment = "ild-pi-agent";
    private const string ExtensionDirSegment = "ild-pi-ext";
    private const string IldToolPrefix = "ild_";
    private const string BridgeFileName = "ild-mcp-bridge.js";
    private const string BridgeResourceName = "ILD.Core.PiExtension.ild-mcp-bridge.js";

    private static string BuildSnapshotPath(string sessionDirectory, string sessionId)
        => Path.Combine(sessionDirectory, $"{SanitizeFileName(sessionId)}.jsonl");

    // The session dir is the agent's to write, so it is listed and read as the
    // agent (see AgentWritableFiles), never by the orchestrator through a link.
    private static async Task<string?> FindSessionFileAsync(string sessionDirectory, string sessionId, CancellationToken ct)
    {
        var files = await AgentWritableFiles.ListFilesAsync(sessionDirectory, "*.jsonl", ct);

        var exactPath = BuildSnapshotPath(sessionDirectory, sessionId);
        if (files.Any(file => file.Path == exactPath))
            return exactPath;

        foreach (var (path, firstLine) in files)
        {
            if (Path.GetFileNameWithoutExtension(path).Contains(sessionId, StringComparison.OrdinalIgnoreCase)
                || SessionHeaderMatches(firstLine, sessionId))
                return path;
        }

        return null;
    }

    private static bool SessionHeaderMatches(string firstLine, string sessionId)
    {
        if (!TryParseJson(firstLine, out var doc))
            return false;

        using (doc!)
        {
            var root = doc!.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && TryGetString(root, "type", out var type)
                && string.Equals(type, "session", StringComparison.OrdinalIgnoreCase)
                && TryGetString(root, "id", out var id)
                && string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(invalidChars.Contains(ch) ? '_' : ch);
        }

        return sb.ToString();
    }

    private static async Task PrepareRuntimeFilesAsync(PiAdapterSettings settings, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(settings.AgentDirectory))
        {
            await AgentWritableFiles.CreateDirectoryAsync(settings.AgentDirectory, ct);

            // Older builds wrote an HTTP-calling ild.ts here, and the agent dir is
            // reused by later turns of the same run or chat. pi loads it before any
            // `-e` path and keeps the first tool of a name, so a leftover would
            // shadow the MCP tools; it also still holds the token of its day.
            // Whatever is there goes, and what cannot go never holds up the launch.
            await AgentWritableFiles.DeleteAsync([LegacyExtensionPath(settings.AgentDirectory)], ct);

            if (!string.IsNullOrWhiteSpace(settings.ModelsJsonContent))
                await AgentWritableFiles.WriteFileAsync(
                    Path.Combine(settings.AgentDirectory, "models.json"), settings.ModelsJsonContent, ct);
        }

        if (!string.IsNullOrWhiteSpace(settings.IldExtensionPath)
            && !string.IsNullOrWhiteSpace(settings.IldExtensionContent))
        {
            AgentIsolation.WriteAgentReadableFile(settings.IldExtensionPath, Encoding.UTF8.GetBytes(settings.IldExtensionContent));
            AgentIsolation.WriteAgentReadableFile(
                Path.Combine(Path.GetDirectoryName(settings.IldExtensionPath)!, BridgeFileName), Bridge.Value);
        }
    }

    private static readonly Lazy<byte[]> Bridge = new(() =>
    {
        using var resource = typeof(PiAdapter).Assembly.GetManifestResourceStream(BridgeResourceName)
            ?? throw new InvalidOperationException($"{BridgeResourceName} is not embedded in ILD.Core");
        using var copy = new MemoryStream();
        resource.CopyTo(copy);
        return copy.ToArray();
    });

    private static string LegacyExtensionPath(string agentDirectory) => Path.Combine(agentDirectory, "extensions", "ild.ts");

    /// <summary>
    /// Remove what pi keeps for a loop run or chat session (chat turns run under the
    /// session id). The ILD extension holds the API token and lives where only the
    /// orchestrator can write, so failing to remove it throws, for the caller to keep
    /// its run or chat and retry. The agent and session directories are the agent's
    /// and may hold anything it planted; they are cleared as the agent and never fail
    /// the caller. Returns whether those are fully gone.
    /// </summary>
    public static async Task<bool> DeleteRunFilesAsync(Guid loopRunId, CancellationToken ct = default)
    {
        var id = loopRunId.ToString("N");
        var extension = Path.Combine(AgentIsolation.AgentReadRoot, ExtensionDirSegment, id);
        if (Directory.Exists(extension))
            Directory.Delete(extension, recursive: true);

        return await AgentWritableFiles.DeleteAsync(
            [Path.Combine(AgentIsolation.ScratchRoot, AgentDirSegment, id), Path.Combine(AgentIsolation.ScratchRoot, SessionDirSegment, id)],
            ct);
    }

    /// <summary>
    /// Delete the HTTP-calling <c>extensions/ild.ts</c> older builds left, with the
    /// token of its day, in every pi agent directory, including those of runs and
    /// chats that will never launch again. Returns whether they are all gone.
    /// </summary>
    internal static Task<bool> SweepLegacyExtensionsAsync(string scratchRoot, CancellationToken ct)
        => AgentWritableFiles.DeleteInSubdirectoriesAsync(
            Path.Combine(scratchRoot, AgentDirSegment), LegacyExtensionPath(string.Empty), ct);

    /// <summary>
    /// The ILD extension directory of every run and chat not in <paramref name="keep"/>
    /// that was written before <paramref name="writtenBeforeUtc"/>, for the startup
    /// sweep to delete. The extensions live in the agent read root, where only the
    /// orchestrator writes, so they are deleted by path.
    ///
    /// The age is half the test, exactly as it is for the MCP configs
    /// (<see cref="IldMcpServer.StaleConfigFiles"/>): what the sweep exists to
    /// collect is what a killed process left, and a directory written since this
    /// process started belongs to a launch happening right now — one whose id no
    /// database knew when the active set was read.
    /// </summary>
    internal static IEnumerable<string> StaleExtensions(string agentReadRoot, IReadOnlySet<Guid> keep, DateTime writtenBeforeUtc)
    {
        var extensions = Path.Combine(agentReadRoot, ExtensionDirSegment);
        return Directory.Exists(extensions)
            ? Directory.EnumerateDirectories(extensions)
                .Where(directory => !(Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) && keep.Contains(id))
                    && Directory.GetLastWriteTimeUtc(directory) < writtenBeforeUtc)
            : [];
    }

    private static PiAdapterSettings ResolveSettings(AiProvider provider, LoopRunContext runContext, IReadOnlyList<string>? selectedToolKeys, Guid? chatSessionId = null)
    {
        var loopRunId = runContext.LoopRunId;
        var config = AiProviderConfig.Parse(provider.Config);
        var binaryPath = config.BinaryPathOr(ManagedAgentInstall.ResolveCommand(ManagedAgentCatalog.Pi));
        var apiKey = config.ApiKey ?? provider.ApiKey;
        var providerName = config.Provider;
        var model = config.Model ?? provider.Model;
        var api = config.Api ?? "openai-completions";
        var hasAbsoluteBaseUrl = Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out _);
        var enabledToolKeys = AiToolCatalog.NormalizeSelectedToolKeys(provider.Type, selectedToolKeys);
        var ildServer = enabledToolKeys.Contains(AiToolCatalog.Ild, StringComparer.OrdinalIgnoreCase)
            ? ClaudeCodeAdapter.BuildIldMcpEntry(runContext, chatSessionId)
            : null;
        var ildServerDll = (ildServer?["args"] as string[])?.FirstOrDefault();
        var toolNames = BuildPiToolNames(enabledToolKeys, ildServerDll);

        // Loaded with `-e` from its own directory rather than from the agent dir,
        // which only exists for an absolute BaseUrl: ILD tools must not depend on it.
        string? ildExtensionPath = null;
        string? ildExtensionContent = null;
        if (ildServer is not null)
        {
            ildExtensionPath = Path.Combine(
                AgentIsolation.CreateAgentReadDirectory(ExtensionDirSegment, loopRunId.ToString("N")), "ild.ts");
            ildExtensionContent = BuildIldExtensionContent(ildServer);
        }

        if (!string.IsNullOrWhiteSpace(provider.BaseUrl)
            && !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out _)
            && !provider.BaseUrl.Contains('/'))
        {
            binaryPath = provider.BaseUrl;
        }

        string? agentDirectory = null;
        string? modelsJsonContent = null;
        string? apiKeyEnvironmentVariableName = null;
        var passApiKeyViaCli = true;

        if (hasAbsoluteBaseUrl)
        {
            providerName ??= BuildSyntheticProviderName(provider);
            model = StripProviderPrefix(model, providerName);

            agentDirectory = Path.Combine(AgentIsolation.ScratchRoot, AgentDirSegment, loopRunId.ToString("N"));
            apiKeyEnvironmentVariableName = "ILD_PI_PROVIDER_API_KEY";
            modelsJsonContent = BuildModelsJson(provider, providerName!, model, api, apiKeyEnvironmentVariableName, apiKey);
            passApiKeyViaCli = false;
        }

        model = StripProviderPrefix(model, providerName);

        return new PiAdapterSettings(
            binaryPath,
            providerName,
            model,
            apiKey,
            passApiKeyViaCli,
            agentDirectory,
            modelsJsonContent,
            apiKeyEnvironmentVariableName,
            ildExtensionPath,
            ildExtensionContent,
            toolNames);
    }

    private static IReadOnlyList<string> BuildPiToolNames(IReadOnlyList<string> enabledToolKeys, string? ildServerDll)
    {
        var enabled = new HashSet<string>(enabledToolKeys, StringComparer.OrdinalIgnoreCase);
        var toolNames = new List<string>();

        if (enabled.Contains(AiToolCatalog.Read))
            toolNames.AddRange(["read", "grep", "find", "ls"]);

        if (enabled.Contains(AiToolCatalog.Write))
            toolNames.AddRange(["edit", "write"]);

        if (enabled.Contains(AiToolCatalog.Execute))
            toolNames.Add("bash");

        if (ildServerDll is not null)
            toolNames.AddRange(IldMcpToolNames.Read(ildServerDll).Select(name => IldToolPrefix + name));

        return toolNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string StripProviderPrefix(string? model, string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(model))
            return model!;

        if (model.StartsWith(providerName + "/", StringComparison.OrdinalIgnoreCase))
            return model[(providerName.Length + 1)..];

        return model;
    }

    private static string BuildSyntheticProviderName(AiProvider provider)
    {
        var seed = provider.Id != Guid.Empty ? provider.Id.ToString("N") : provider.Name;
        seed = string.IsNullOrWhiteSpace(seed) ? "provider" : seed;
        return "ild-" + SanitizeProviderKey(seed);
    }

    private static string SanitizeProviderKey(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        }

        return sb.ToString().Trim('-');
    }

    private static string BuildModelsJson(
        AiProvider provider,
        string providerName,
        string model,
        string api,
        string apiKeyEnvironmentVariableName,
        string? apiKey)
    {
        var providerNode = new JsonObject
        {
            ["baseUrl"] = provider.BaseUrl,
            ["api"] = api,
            // Pi treats a plain uppercase apiKey (e.g. "ILD_PI_PROVIDER_API_KEY")
            // as a *literal* key; a "$"-prefix tells it to interpolate the named
            // environment variable instead. We set that env var to the real key
            // in BuildRunProcessStartInfo, so reference it with "$" — otherwise
            // Pi sends the literal variable name as the credential and the
            // backend (e.g. vLLM) rejects it with 401. The "ild" literal mirrors
            // Pi's own "apiKey": "ollama" placeholder for keyless backends.
            ["apiKey"] = string.IsNullOrWhiteSpace(apiKey) ? "ild" : "$" + apiKeyEnvironmentVariableName,
            ["models"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = model,
                }
            }
        };

        return new JsonObject
        {
            ["providers"] = new JsonObject
            {
                [providerName] = providerNode,
            }
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Build the <c>ild.ts</c> pi extension: it hands the ILD MCP server's launch
    /// entry (<see cref="ClaudeCodeAdapter.BuildIldMcpEntry"/>) to
    /// <c>ild-mcp-bridge.js</c> (written beside it), which registers every tool the
    /// server lists as <c>ild_&lt;name&gt;</c>. The truncation utilities are passed
    /// in because only an extension can import them from pi.
    /// </summary>
    private static string BuildIldExtensionContent(Dictionary<string, object?> ildServer)
    {
        var config = JsonSerializer.Serialize(new Dictionary<string, object?>(ildServer) { ["toolPrefix"] = IldToolPrefix });

        return $$"""
            import { DEFAULT_MAX_BYTES, DEFAULT_MAX_LINES, formatSize, truncateHead } from "@earendil-works/pi-coding-agent";
            import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
            import { registerIldMcpTools } from "./{{BridgeFileName}}";

            const CONFIG = {{config}};

            export default async function (pi: ExtensionAPI) {
                await registerIldMcpTools(pi, {
                    ...CONFIG,
                    truncate: { truncateHead, formatSize, DEFAULT_MAX_BYTES, DEFAULT_MAX_LINES },
                });
            }

            """;
    }

    internal sealed record PiAdapterSettings(
        string BinaryPath,
        string? Provider,
        string Model,
        string? ApiKey,
        bool PassApiKeyViaCli,
        string? AgentDirectory,
        string? ModelsJsonContent,
        string? ApiKeyEnvironmentVariableName,
        string? IldExtensionPath,
        string? IldExtensionContent,
        IReadOnlyList<string> ToolNames);

    private sealed record PiExecutionOutput(string RawStdout, string Content, string? SessionId, bool SawJsonEvents, bool SawTurnEnd);

    private sealed record ManagedSessionRestoreResult(string? SessionIdToUse, string? SessionPathToUse)
    {
        public static ManagedSessionRestoreResult Use(string sessionId, string sessionPath)
            => new(sessionId, sessionPath);

        public static ManagedSessionRestoreResult StartFresh()
            => new(null, null);
    }
}