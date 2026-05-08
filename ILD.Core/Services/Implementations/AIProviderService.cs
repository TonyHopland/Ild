using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// OpenAI-compatible chat completion client + simple template renderer.
/// </summary>
public class AIProviderService : IAIProviderService
{
    private readonly IProviderStore _providerStore;
    private readonly IWorkItemManager _workItemManager;
    private readonly HttpClient _http;
    private readonly IPromptTemplateResolver _resolver;

    public AIProviderService(IProviderStore providerStore, IWorkItemManager workItemManager, HttpClient http, IPromptTemplateResolver? resolver = null)
    {
        _providerStore = providerStore;
        _workItemManager = workItemManager;
        _http = http;
        _resolver = resolver ?? new PromptTemplateResolver();
    }

    public async Task<string> CompleteAsync(string prompt, string? providerId = null, CancellationToken cancellationToken = default)
    {
        var provider = await ResolveProviderAsync(providerId);
        if (provider == null) return $"[no-provider] {prompt}";

        var requestUri = provider.BaseUrl.TrimEnd('/') + "/chat/completions";
        var body = new
        {
            model = provider.Model,
            messages = new[] { new { role = "user", content = prompt } },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrEmpty(provider.ApiKey))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
        try
        {
            using var resp = await _http.SendAsync(req, cancellationToken);
            resp.EnsureSuccessStatusCode();
            var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AiProviderException($"AI provider call failed: {ex.Message}", ex);
        }
    }

    public Task<string> RenderPromptAsync(string template, LoopRunContext context)
    {
        var rendered = _resolver.Render(template, new PromptContext(
            WorkItemTitle: context.WorkItemTitle,
            WorkItemDescription: context.WorkItemDescription,
            PreviousNodeOutput: context.PreviousNodeOutput,
            EventLogSummary: context.EventLogSummary,
            WorktreePath: context.WorktreePath));
        return Task.FromResult(rendered);
    }

    public Task<bool> ValidatePromptTemplateAsync(string template)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WorkItem.Title","WorkItem.Description","WorkTree.Diff",
            "EventLog.Summary","EventLog.LastN","Node.Input","PreviousNode.Output",
        };
        foreach (Match m in PlaceholderPattern.Matches(template))
        {
            var name = m.Groups[1].Value;
            if (!known.Contains(name) && !name.StartsWith("WorkTree.File:", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    private static readonly Regex PlaceholderPattern =
        new(@"\{\{\s*([A-Za-z][A-Za-z0-9_.:/\\-]*)\s*\}\}", RegexOptions.Compiled);

    public async Task<IEnumerable<string>> GetAvailableProvidersAsync()
        => await _providerStore.GetAiProviderNamesAsync();

    public Task<IEnumerable<string>> GetAvailableToolsAsync()
        => Task.FromResult<IEnumerable<string>>(new[]
        {
            "shell.exec","file.read","file.write","git.diff","ild.create_workitem"
        });

    public async Task<ToolExecutionResult> ExecuteToolAsync(string toolName, string arguments, string worktreePath)
    {
        try
        {
            switch (toolName)
            {
                case "shell.exec":
                    return await RunShellAsync(arguments, worktreePath);
                case "file.read":
                {
                    var safe = SafePath(worktreePath, arguments);
                    return safe == null
                        ? new ToolExecutionResult(false, "", "path traversal", -1)
                        : new ToolExecutionResult(true, await File.ReadAllTextAsync(safe), null);
                }
                case "file.write":
                {
                    var doc = JsonDocument.Parse(arguments);
                    var path = doc.RootElement.GetProperty("path").GetString() ?? "";
                    var content = doc.RootElement.GetProperty("content").GetString() ?? "";
                    var safe = SafePath(worktreePath, path);
                    if (safe == null) return new ToolExecutionResult(false, "", "path traversal", -1);
                    Directory.CreateDirectory(Path.GetDirectoryName(safe)!);
                    await File.WriteAllTextAsync(safe, content);
                    return new ToolExecutionResult(true, "ok", null);
                }
                case "git.diff":
                    return await RunShellAsync("git diff HEAD", worktreePath);
                case "ild.create_workitem":
                    return await CreateWorkItemAsync(arguments);
                default:
                    return new ToolExecutionResult(false, "", $"unknown tool {toolName}", -1);
            }
        }
        catch (Exception ex) { return new ToolExecutionResult(false, "", ex.Message, -1); }
    }

    private async Task<AiProvider?> ResolveProviderAsync(string? providerId)
    {
        if (Guid.TryParse(providerId, out var id))
            return await _providerStore.GetAiProviderByIdAsync(id);
        if (!string.IsNullOrEmpty(providerId))
            return await _providerStore.GetAiProviderByNameAsync(providerId);
        return await _providerStore.GetDefaultAiProviderAsync()
            ?? await _providerStore.GetFirstAiProviderAsync();
    }

    private static string? SafePath(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rootFull = Path.GetFullPath(root);
        return full.StartsWith(rootFull, StringComparison.Ordinal) ? full : null;
    }

    private static async Task<ToolExecutionResult> RunShellAsync(string command, string cwd)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = Directory.Exists(cwd) ? cwd : Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return new ToolExecutionResult(proc.ExitCode == 0, stdout, proc.ExitCode == 0 ? null : stderr, proc.ExitCode);
    }

    private async Task<ToolExecutionResult> CreateWorkItemAsync(string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
            if (string.IsNullOrEmpty(title))
                return new ToolExecutionResult(false, "", "missing required field: title", -1);
            var description = root.TryGetProperty("description", out var descProp) ? descProp.GetString() : "";
            // Legacy `loopTemplateId` is ignored — template is resolved from
            // tags at run start (PRD §3.7).
            Guid? repositoryId = null;
            if (root.TryGetProperty("repositoryId", out var repoProp) && Guid.TryParse(repoProp.GetString(), out var repoId))
                repositoryId = repoId;
            var id = await _workItemManager.CreateWorkItemAsync(title!, description ?? "", repositoryId);
            return new ToolExecutionResult(true, id.ToString(), null);
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult(false, "", ex.Message, -1);
        }
    }
}
