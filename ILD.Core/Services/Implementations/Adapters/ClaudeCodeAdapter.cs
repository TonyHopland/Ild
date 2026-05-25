using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Runs Anthropic's <c>claude</c> CLI in headless mode. Authentication is
/// expected to be set up out-of-band (e.g. <c>claude /login</c> for a Max
/// subscription, which writes credentials to <c>~/.claude</c>). The adapter
/// intentionally ignores <see cref="AiProvider.BaseUrl"/>,
/// <see cref="AiProvider.ApiKey"/> and <see cref="AiProvider.Model"/> — those
/// fields are not meaningful for subscription-based auth.
/// </summary>
public sealed class ClaudeCodeAdapter : IAgentAdapter
{
    private static readonly IPromptTemplateResolver Resolver = new PromptTemplateResolver();

    public string Name => "ClaudeCode";
    public string[] SupportedProviderTypes => ["claude-code"];
    public ConfigFieldDescriptor[] ConfigSchema => Array.Empty<ConfigFieldDescriptor>();

    public async Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
    {
        try
        {
            var rendered = await RenderPromptAsync(ctx.Prompt, ctx.RunContext);
            var binaryPath = ResolveBinaryPath(ctx.Provider);

            var worktreePath = ctx.RunContext.WorktreePath;
            if (string.IsNullOrEmpty(worktreePath) || !Directory.Exists(worktreePath))
                return NodeExecutionResult.Fail(
                    "[claude-code-error] AI node requires a valid worktree path; refusing to run outside the loop's worktree.");

            Process? proc;
            try
            {
                proc = Process.Start(BuildRunProcessStartInfo(binaryPath, worktreePath, rendered, ctx.SessionId));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                return NodeExecutionResult.Fail(
                    $"[claude-code-error] cannot start '{binaryPath}' — make sure the claude CLI is installed and on PATH, and that you are logged in (e.g. via 'claude /login'). Details: {ex.Message}");
            }

            using var process = proc ?? throw new InvalidOperationException("Process.Start returned null");

            var stdoutTask = ReadStreamJsonAsync(process.StandardOutput, ctx.ProgressCallback, ctx.Cancel);
            var stderrTask = process.StandardError.ReadToEndAsync(ctx.Cancel);

            try
            {
                await process.WaitForExitAsync(ctx.Cancel);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
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
                try { process.Kill(entireProcessTree: true); } catch { }
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

            return process.ExitCode == 0
                ? NodeExecutionResult.Ok(response, rendered, effectiveSessionId, ctx.IncomingSessionId)
                : NodeExecutionResult.Fail($"exit={process.ExitCode} stderr={stderr}", response);
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Fail($"[claude-code-error] {ex.Message}");
        }
    }

    public static ProcessStartInfo BuildRunProcessStartInfo(
        string binaryPath,
        string worktreePath,
        string renderedPrompt,
        string? sessionId)
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

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(sessionId);
        }

        psi.ArgumentList.Add(renderedPrompt);
        return psi;
    }

    private static string ResolveBinaryPath(AiProvider provider)
    {
        var binaryPath = "claude";
        if (string.IsNullOrEmpty(provider.Config)) return binaryPath;

        try
        {
            using var doc = JsonDocument.Parse(provider.Config);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("binaryPath", out var bp)
                && bp.ValueKind == JsonValueKind.String)
            {
                var value = bp.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value!;
            }
        }
        catch (JsonException) { }

        return binaryPath;
    }

    private static async Task<ClaudeStreamOutput> ReadStreamJsonAsync(
        StreamReader reader,
        Func<string, Task>? progressCallback,
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
                sessionId = sid;

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

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
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

    private static Task<string> RenderPromptAsync(string template, LoopRunContext context)
        => Task.FromResult(Resolver.Render(template, new PromptContext(
            WorkItemTitle: context.WorkItemTitle,
            WorkItemDescription: context.WorkItemDescription,
            PreviousNodeOutput: context.PreviousNodeOutput,
            EventLogSummary: context.EventLogSummary,
            WorktreePath: context.WorktreePath)));

    private sealed record ClaudeStreamOutput(
        string RawStdout,
        string Content,
        string? SessionId,
        string? ErrorMessage,
        bool SawJsonEvents);
}
