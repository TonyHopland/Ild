using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations.Adapters;

public class OpenCodeAdapter : IAgentAdapter
{
    private static readonly Regex Placeholder = new(
        @"\{\{\s*([A-Za-z][A-Za-z0-9_.:/\\-]*)\s*\}\}", RegexOptions.Compiled);

    public string Name => "OpenCode";
    public string[] SupportedProviderTypes => ["opencode"];
    public ConfigFieldDescriptor[] ConfigSchema => new ConfigFieldDescriptor[]
    {
        new("binaryPath", ConfigFieldType.Text, "Binary Path", false, "opencode", "Path to the opencode binary"),
        new("timeoutSeconds", ConfigFieldType.Number, "Timeout (seconds)", false, 300, "Maximum execution time"),
    };

    public async Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
    {
        try
        {
            var prompt = ctx.ExecutionCount == 1 ? ctx.InitialPrompt : ctx.LoopPrompt;
            var rendered = await RenderPromptAsync(prompt, ctx.RunContext);

            var (binaryPath, timeoutSeconds) = ResolveConfig(ctx.Provider);

            if (string.IsNullOrEmpty(binaryPath))
                return NodeExecutionResult.Fail("[opencode-error] binaryPath is not configured");

            var psi = new ProcessStartInfo(binaryPath)
            {
                WorkingDirectory = string.IsNullOrEmpty(ctx.RunContext.WorktreePath)
                    ? Directory.GetCurrentDirectory()
                    : ctx.RunContext.WorktreePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var (opencodeModel, opencodeConfigJson) = BuildOpenCodeConfig(ctx.Provider);

            psi.EnvironmentVariables["OPENCODE_CONFIG_CONTENT"] = opencodeConfigJson;

            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--dir");
            psi.ArgumentList.Add(
                string.IsNullOrEmpty(ctx.RunContext.WorktreePath)
                    ? Directory.GetCurrentDirectory()
                    : ctx.RunContext.WorktreePath);
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(opencodeModel);
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add("--dangerously-skip-permissions");
            psi.ArgumentList.Add(rendered);

            Process? proc = null;
            try
            {
                proc = Process.Start(psi);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                return NodeExecutionResult.Fail($"[opencode-error] cannot start '{binaryPath}' — make sure the opencode binary is installed and on PATH. Details: {ex.Message}");
            }
            using var p = proc ?? throw new InvalidOperationException("Process.Start returned null");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancel);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var stdoutLines = new List<string>();
            var stderrLines = new List<string>();
            var stdoutLock = new object();
            var stderrLock = new object();

            var stdoutTask = ReadAndStreamLinesAsync(p.StandardOutput, stdoutLines, stdoutLock, ctx.ProgressCallback, timeoutCts.Token);
            var stderrTask = ReadAndStreamLinesAsync(p.StandardError, stderrLines, stderrLock, ctx.ProgressCallback, timeoutCts.Token);

            try
            {
                await p.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return NodeExecutionResult.Fail($"opencode timed out after {timeoutSeconds}s");
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
                return NodeExecutionResult.Fail($"opencode stream read timed out after {timeoutSeconds}s");
            }
            var response = ExtractTextFromJsonEvents(stdout);
            if (string.IsNullOrEmpty(response))
                response = stdout;

            return p.ExitCode == 0
                ? NodeExecutionResult.Ok(response)
                : NodeExecutionResult.Fail($"exit={p.ExitCode} stderr={stderr}", response);
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Fail($"[opencode-error] {ex.Message}");
        }
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
                        sb.Append(prop.Value.GetString());
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

    private static (string BinaryPath, int TimeoutSeconds) ResolveConfig(AiProvider provider)
    {
        var binaryPath = "opencode";
        var timeoutSeconds = 300;

        if (!string.IsNullOrEmpty(provider.Config))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(provider.Config);
                var root = doc.RootElement;
                if (root.TryGetProperty("binaryPath", out var bp) && bp.ValueKind == System.Text.Json.JsonValueKind.String)
                    binaryPath = bp.GetString() ?? binaryPath;
                if (root.TryGetProperty("timeoutSeconds", out var ts) && ts.ValueKind == System.Text.Json.JsonValueKind.Number)
                    timeoutSeconds = ts.GetInt32();
            }
            catch { }
        }

        return (binaryPath, timeoutSeconds);
    }

    private static (string ModelRef, string ConfigJson) BuildOpenCodeConfig(AiProvider provider)
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

        var configJson = JsonSerializer.Serialize(config);
        var modelRef = $"{providerId}/{modelId}";
        return (modelRef, configJson);
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
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["WorkItem.Title"] = context.WorkItemTitle,
            ["WorkItem.Description"] = context.WorkItemDescription,
            ["WorkTree.Diff"] = "",
            ["EventLog.Summary"] = string.Join("\n", context.EventLogSummary ?? new()),
            ["EventLog.LastN"] = string.Join("\n", (context.EventLogSummary ?? new()).TakeLast(10)),
            ["Node.Input"] = context.PreviousNodeOutput ?? "",
            ["PreviousNode.Output"] = context.PreviousNodeOutput ?? "",
        };

        var rendered = Placeholder.Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (values.TryGetValue(key, out var v)) return v ?? "";
            if (key.StartsWith("WorkTree.File:", StringComparison.OrdinalIgnoreCase))
            {
                var rel = key.Substring("WorkTree.File:".Length);
                var full = string.IsNullOrEmpty(context.WorktreePath) ? null : Path.Combine(context.WorktreePath, rel);
                return full != null && File.Exists(full) ? File.ReadAllText(full) : "";
            }
            return m.Value;
        });
        return Task.FromResult(rendered);
    }

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
                await progressCallback(line).ConfigureAwait(false);
        }
        lock (lockObj) return string.Join("\n", lines);
    }
}
