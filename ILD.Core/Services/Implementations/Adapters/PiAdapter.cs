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
            var rendered = await RenderPromptAsync(ctx.Prompt, ctx.RunContext);
            var settings = ResolveSettings(ctx.Provider, ctx.RunContext.LoopRunId, ctx.ToolAllowlist, ctx.ChatSessionId);

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

            var sessionDirectory = BuildSessionDirectory(ctx.RunContext.LoopRunId);
            Directory.CreateDirectory(sessionDirectory);
            PrepareRuntimeFiles(settings);

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
                proc = Process.Start(BuildRunProcessStartInfo(
                    settings,
                    worktreePath,
                    rendered,
                    sessionDirectory,
                    sessionIdToUse,
                    sessionPathToUse));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                return NodeExecutionResult.Fail($"[pi-error] cannot start '{settings.BinaryPath}' — install or update Pi from the AI Provider page, or make sure the pi binary is on PATH. Details: {ex.Message}");
            }

            using var process = proc ?? throw new InvalidOperationException("Process.Start returned null");
            try
            {
                await process.StandardInput.WriteAsync(rendered.AsMemory(), ctx.Cancel);
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
                KillProcessTree(process);
                return NodeExecutionResult.Fail("pi timed out");
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
                KillProcessTree(process);
                return NodeExecutionResult.Fail("pi stream read timed out");
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
                ? NodeExecutionResult.Ok(response, rendered, effectiveSessionId, ctx.IncomingSessionId, AdapterUsageParser.Parse(stdout.RawStdout))
                : NodeExecutionResult.Fail($"exit={process.ExitCode} stderr={stderr}", response);
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Fail($"[pi-error] {ex.Message}");
        }
    }

    internal static ProcessStartInfo BuildRunProcessStartInfo(
        PiAdapterSettings settings,
        string worktreePath,
        string renderedPrompt,
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
        var localSessionPath = FindSessionFile(sessionDirectory, sessionId);
        if (!string.IsNullOrWhiteSpace(localSessionPath))
            return ManagedSessionRestoreResult.Use(sessionId, localSessionPath);

        if (ScopeFactory is null)
            return ManagedSessionRestoreResult.StartFresh();

        var snapshot = await GetSnapshotAsync(ctx, sessionId, ctx.Cancel);
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.SessionJson))
            return ManagedSessionRestoreResult.StartFresh();

        var restoredPath = BuildSnapshotPath(sessionDirectory, sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(restoredPath)!);
        await File.WriteAllTextAsync(restoredPath, snapshot.SessionJson, ctx.Cancel);
        return ManagedSessionRestoreResult.Use(sessionId, restoredPath);
    }

    private async Task PersistManagedSessionAsync(string sessionDirectory, string sessionId, AgentExecutionContext ctx)
    {
        if (ScopeFactory is null)
            return;

        var sessionPath = FindSessionFile(sessionDirectory, sessionId);
        if (string.IsNullOrWhiteSpace(sessionPath) || !File.Exists(sessionPath))
            return;

        var sessionJson = await File.ReadAllTextAsync(sessionPath, ctx.Cancel);

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

    private static string BuildSessionDirectory(Guid loopRunId)
        => Path.Combine(Path.GetTempPath(), "ild-pi-sessions", loopRunId.ToString("N"));

    private static string BuildSnapshotPath(string sessionDirectory, string sessionId)
        => Path.Combine(sessionDirectory, $"{SanitizeFileName(sessionId)}.jsonl");

    private static string? FindSessionFile(string sessionDirectory, string sessionId)
    {
        if (!Directory.Exists(sessionDirectory))
            return null;

        var exactPath = BuildSnapshotPath(sessionDirectory, sessionId);
        if (File.Exists(exactPath))
            return exactPath;

        foreach (var path in Directory.EnumerateFiles(sessionDirectory, "*.jsonl", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (string.Equals(fileName, sessionId, StringComparison.OrdinalIgnoreCase)
                || fileName.Contains(sessionId, StringComparison.OrdinalIgnoreCase))
                return path;

            if (SessionFileHeaderMatches(path, sessionId))
                return path;
        }

        return null;
    }

    private static bool SessionFileHeaderMatches(string path, string sessionId)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream);
            var firstLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(firstLine))
                return false;

            using var doc = JsonDocument.Parse(firstLine);
            var root = doc.RootElement;
            return TryGetString(root, "type", out var type)
                && string.Equals(type, "session", StringComparison.OrdinalIgnoreCase)
                && TryGetString(root, "id", out var id)
                && string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
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

    private static string BuildAgentDirectory(Guid loopRunId)
        => Path.Combine(Path.GetTempPath(), "ild-pi-agent", loopRunId.ToString("N"));

    private static void PrepareRuntimeFiles(PiAdapterSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AgentDirectory)
            || string.IsNullOrWhiteSpace(settings.ModelsJsonContent))
            return;

        Directory.CreateDirectory(settings.AgentDirectory);
        File.WriteAllText(Path.Combine(settings.AgentDirectory, "models.json"), settings.ModelsJsonContent);

        // Write the ILD extension so Pi can list/create work items via its tool system.
        if (!string.IsNullOrWhiteSpace(settings.IldExtensionContent))
        {
            var extensionsDir = Path.Combine(settings.AgentDirectory, "extensions");
            Directory.CreateDirectory(extensionsDir);
            File.WriteAllText(Path.Combine(extensionsDir, "ild.ts"), settings.IldExtensionContent);
        }
    }

    private static PiAdapterSettings ResolveSettings(AiProvider provider, Guid loopRunId, IReadOnlyList<string>? selectedToolKeys, Guid? chatSessionId = null)
    {
        var config = AiProviderConfig.Parse(provider.Config);
        var binaryPath = config.BinaryPathOr(ManagedAgentInstall.ResolveCommand(ManagedAgentCatalog.Pi));
        var apiKey = config.ApiKey ?? provider.ApiKey;
        var providerName = config.Provider;
        var model = config.Model ?? provider.Model;
        var api = config.Api ?? "openai-completions";
        var hasAbsoluteBaseUrl = Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out _);
        var enabledToolKeys = AiToolCatalog.NormalizeSelectedToolKeys(provider.Type, selectedToolKeys);
        var toolNames = BuildPiToolNames(enabledToolKeys);

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

            agentDirectory = BuildAgentDirectory(loopRunId);
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
            BuildIldExtensionContent(hasAbsoluteBaseUrl, loopRunId, enabledToolKeys, chatSessionId),
            toolNames);
    }

    private static IReadOnlyList<string> BuildPiToolNames(IReadOnlyList<string> enabledToolKeys)
    {
        var enabled = new HashSet<string>(enabledToolKeys, StringComparer.OrdinalIgnoreCase);
        var toolNames = new List<string>();

        if (enabled.Contains(AiToolCatalog.Read))
            toolNames.AddRange(["read", "grep", "find", "ls"]);

        if (enabled.Contains(AiToolCatalog.Write))
            toolNames.AddRange(["edit", "write"]);

        if (enabled.Contains(AiToolCatalog.Execute))
            toolNames.Add("bash");

        if (enabled.Contains(AiToolCatalog.Ild))
            toolNames.AddRange(ToolDescriptors.All.Select(tool => tool.Name));

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
            ["apiKey"] = string.IsNullOrWhiteSpace(apiKey) ? "ild" : apiKeyEnvironmentVariableName,
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
    /// Build the TypeScript content for the ILD Pi extension that registers
    /// tools for interacting with the ILD platform API.
    /// Delegates to <see cref="PiExtensionGenerator"/> which reads from the shared
    /// <see cref="ILD.Data.ToolDescriptors"/> to avoid duplicating tool definitions.
    /// </summary>
    private static string? BuildIldExtensionContent(bool shouldGenerate, Guid loopRunId, IReadOnlyList<string> enabledToolKeys, Guid? chatSessionId = null)
    {
        if (!shouldGenerate || !enabledToolKeys.Contains(AiToolCatalog.Ild, StringComparer.OrdinalIgnoreCase))
            return null;

        var apiUrl = Environment.GetEnvironmentVariable("ILD_API_URL")
            ?? "http://localhost:5000";
        var apiToken = Environment.GetEnvironmentVariable("ILD_API_TOKEN")
            ?? string.Empty;

        var contextId = chatSessionId?.ToString() ?? loopRunId.ToString();

        return PiExtensionGenerator.Generate(
            apiUrl,
            apiToken,
            contextId,
            ToolDescriptors.All.Select(tool => tool.Name).ToArray(),
            isChatSession: chatSessionId is not null);
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