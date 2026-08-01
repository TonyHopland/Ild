using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// OpenAI-compatible chat completion client, template validation and tool execution.
/// </summary>
public class AIProviderService : IAIProviderService
{
    private readonly IProviderStore _providerStore;
    private readonly IWorkItemManager _workItemManager;
    private readonly IWorktreePreviewService _worktreePreviewService;
    private readonly HttpClient _http;
    private readonly ILoopRunStore? _loopRuns;
    private readonly ILogger<AIProviderService>? _logger;

    public AIProviderService(IProviderStore providerStore, IWorkItemManager workItemManager, IWorktreePreviewService worktreePreviewService, HttpClient http, ILoopRunStore? loopRuns = null, ILogger<AIProviderService>? logger = null)
    {
        _providerStore = providerStore;
        _workItemManager = workItemManager;
        _worktreePreviewService = worktreePreviewService;
        _http = http;
        _loopRuns = loopRuns;
        _logger = logger;
    }

    private ToolExecutionResult ToolFailure(string toolName, Exception ex)
    {
        _logger?.LogWarning(ex, "Tool '{Tool}' failed: {Message}", toolName, ex.Message);
        return new ToolExecutionResult(false, "", ex.Message, -1);
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

    public Task<bool> ValidatePromptTemplateAsync(string template)
    {
        foreach (Match m in PromptPlaceholderRegistry.Pattern.Matches(template))
        {
            var name = m.Groups[1].Value;
            if (!PromptPlaceholderRegistry.IsKnown(name))
                return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    public async Task<IEnumerable<string>> GetAvailableProvidersAsync()
        => await _providerStore.GetAiProviderNamesAsync();

    public Task<IEnumerable<string>> GetAvailableToolsAsync()
        => Task.FromResult<IEnumerable<string>>(new[]
        {
            "shell.exec","file.read","file.write","git.diff","ild.create_workitem","ild.preview_start","ild.preview_status","ild.preview_stop"
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
                {
                    // Scope the diff to what this run added on top of the default branch's
                    // fork point. `origin/HEAD` resolves to origin/<defaultBranch>; merge-base
                    // pins the fork point so subsequent fast-forwards on the default branch
                    // don't drag unrelated commits into the diff. `--intent-to-add` makes new
                    // untracked files appear without staging their contents.
                    var mb = await RunShellAsync("git merge-base HEAD origin/HEAD", worktreePath);
                    var baseRef = mb.Success && !string.IsNullOrWhiteSpace(mb.Output)
                        ? mb.Output.Trim()
                        : "origin/HEAD";
                    await RunShellAsync("git add --intent-to-add .", worktreePath);
                    return await RunShellAsync($"git diff {baseRef}", worktreePath);
                }
                case "ild.create_workitem":
                    return await CreateWorkItemAsync(arguments);
                case "ild.preview_start":
                    return await StartPreviewAsync(arguments, worktreePath);
                case "ild.preview_status":
                    return await GetPreviewStatusAsync(worktreePath);
                case "ild.preview_stop":
                    return await StopPreviewAsync(worktreePath);
                default:
                    return new ToolExecutionResult(false, "", $"unknown tool {toolName}", -1);
            }
        }
        catch (Exception ex) { return ToolFailure(toolName, ex); }
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
        // Runs a model-authored command as the orchestrator, so it must not
        // inherit the orchestrator's ambient capabilities — same reasoning as the
        // preview spawn sites (ADR-0014). Effective CAP_SETUID in a hijacked
        // orchestrator-side command is the difference between "runs as ild" and
        // "runs as container root".
        using var proc = Process.Start(AgentIsolation.DropInheritedCapabilities(psi))!;
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
            return ToolFailure("ild.create_workitem", ex);
        }
    }

    private async Task<ToolExecutionResult> StartPreviewAsync(string arguments, string worktreePath)
    {
        try
        {
            string? profileName = null;
            bool skipInstall = false;
            string? publicHost = null;
            Dictionary<string, int>? portOverrides = null;

            if (!string.IsNullOrWhiteSpace(arguments))
            {
                using var doc = JsonDocument.Parse(arguments);
                var root = doc.RootElement;
                if (root.TryGetProperty("profileName", out var profileProp))
                    profileName = profileProp.GetString();
                if (root.TryGetProperty("skipInstall", out var skipProp) && skipProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    skipInstall = skipProp.GetBoolean();
                if (root.TryGetProperty("publicHost", out var hostProp))
                    publicHost = hostProp.GetString();
                if (root.TryGetProperty("portOverrides", out var portsProp) && portsProp.ValueKind == JsonValueKind.Object)
                {
                    portOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in portsProp.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var port))
                            portOverrides[property.Name] = port;
                    }
                }
            }

            var owner = await ResolvePreviewOwnerAsync(worktreePath);
            var status = await _worktreePreviewService.StartAsync(
                worktreePath,
                new WorktreePreviewStartOptions(profileName, skipInstall, publicHost, portOverrides,
                    owner.CustomEnv, owner.WorkItemId));
            return new ToolExecutionResult(true, JsonSerializer.Serialize(status), null);
        }
        catch (Exception ex)
        {
            return ToolFailure("ild.preview_start", ex);
        }
    }

    // The agent tool surface only carries the worktree path, so resolve the repo
    // (and its custom .env) back through the run that owns the worktree —
    // worktree → run → work item → repository. This keeps an agent-started preview
    // injecting the same secrets the human WorkItems/Agent controllers and the run's
    // Start node do. The work item id comes back from the same walk, since the
    // preview needs it to advertise a wi-{id} proxy URL — an agent-started preview
    // must get the same URL a human-started one does. Best-effort: without a run
    // store wired, or an unmatched path, neither is resolved.
    private async Task<(string? WorkItemId, string? CustomEnv)> ResolvePreviewOwnerAsync(string worktreePath)
    {
        if (_loopRuns is null) return (null, null);
        var run = await _loopRuns.GetByWorktreePathAsync(worktreePath);
        if (run is null || string.IsNullOrEmpty(run.WorkItemId)) return (null, null);
        var workItem = await _workItemManager.GetWorkItemAsync(run.WorkItemId);
        return (run.WorkItemId, await _providerStore.GetRepositoryPreviewEnvAsync(workItem?.RepositoryId));
    }

    private async Task<ToolExecutionResult> GetPreviewStatusAsync(string worktreePath)
    {
        try
        {
            var status = await _worktreePreviewService.GetStatusAsync(worktreePath);
            return new ToolExecutionResult(true, JsonSerializer.Serialize(status), null);
        }
        catch (Exception ex)
        {
            return ToolFailure("ild.preview_status", ex);
        }
    }

    private async Task<ToolExecutionResult> StopPreviewAsync(string worktreePath)
    {
        try
        {
            var status = await _worktreePreviewService.StopAsync(worktreePath);
            return new ToolExecutionResult(true, JsonSerializer.Serialize(status), null);
        }
        catch (Exception ex)
        {
            return ToolFailure("ild.preview_stop", ex);
        }
    }
}
