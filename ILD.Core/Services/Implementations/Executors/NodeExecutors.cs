using System.Diagnostics;
using System.Text.Json;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations.Executors;

public sealed class StartNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Start;
    private readonly IRepositoryManager _repo;
    private readonly IServiceProvider _sp;

    public StartNodeExecutor(IRepositoryManager repo, IServiceProvider sp)
    {
        _repo = repo;
        _sp = sp;
    }

    public string DescribeInput(NodeExecutionContext ctx)
        => JsonSerializer.Serialize(new { nodeType = NodeType.ToString(), message = "initialized" });

    public async Task<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        using var scope = _sp.CreateScope();
        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
        var providerStore = scope.ServiceProvider.GetRequiredService<IProviderStore>();

        var wi = await workItemStore.GetByIdAsync(ctx.WorkItem.Id);
        if (wi == null) return new NodeOutcome.Failed("WorkItem not found");
        var repo = await providerStore.GetRepositoryByIdAsync(wi.RepositoryId);
        if (repo == null)
            return new NodeOutcome.Failed(
                "WorkItem has no repository attached; refusing to run loop without an isolated worktree.");

        if (string.IsNullOrEmpty(wi.WorktreePath) || !Directory.Exists(wi.WorktreePath))
        {
            var branch = wi.BranchName ?? $"ild/wi-{wi.Id:N}";
            var basePath = repo.WorktreesPath;
            var cloned = false;
            if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(Path.Combine(basePath, ".git")))
            {
                basePath = Path.GetFullPath(Path.Combine("data", "repos", repo.Id.ToString("N")));
                Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
                if (!Directory.Exists(Path.Combine(basePath, ".git")))
                {
                    var psi = new ProcessStartInfo("git")
                    {
                        WorkingDirectory = Path.GetDirectoryName(basePath)!,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    };
                    psi.ArgumentList.Add("clone");
                    psi.ArgumentList.Add(repo.CloneUrl);
                    psi.ArgumentList.Add(basePath);
                    using var p = Process.Start(psi)!;
                    var stderr = await p.StandardError.ReadToEndAsync();
                    await p.WaitForExitAsync();
                    if (p.ExitCode != 0)
                        return new NodeOutcome.Failed($"git clone failed: {stderr}");
                    cloned = true;
                }
            }
            // Pull existing base repo to ensure it's up to date (best-effort, skip if just cloned)
            if (!cloned)
            {
                try { await _repo.PullAsync(basePath, ctx.CancellationToken); } catch { }
            }
            var path = await _repo.CreateWorktreeAsync(basePath, branch);
            wi.WorktreePath = path;
            wi.BranchName = branch;
            await workItemStore.UpdateAsync(wi);
        }
        if (string.IsNullOrEmpty(wi.WorktreePath) || !Directory.Exists(wi.WorktreePath))
            return new NodeOutcome.Failed(
                $"Failed to materialize worktree for WorkItem {wi.Id}; refusing to continue.");
        return new NodeOutcome.Succeeded($"worktree={wi.WorktreePath}");
    }
}

public sealed class CmdNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Cmd;

    public string DescribeInput(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Cmd>(ctx.Node.Config);
        return JsonSerializer.Serialize(new { nodeType = NodeType.ToString(), command = cfg.Command ?? "(no command)" });
    }

    public async Task<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Cmd>(ctx.Node.Config);
        if (string.IsNullOrEmpty(cfg.Command))
            return new NodeOutcome.Failed("Cmd node missing 'command' config");
        var command = cfg.Command;

        var cwd = ctx.WorkItem.WorktreePath;
        if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
            return new NodeOutcome.Failed(
                "Cmd node requires a valid WorkItem.WorktreePath; refusing to run outside the loop's worktree.");

        var psi = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        using var proc = Process.Start(psi)!;
        var timeout = TimeSpan.FromSeconds(ctx.Node.TimeoutSeconds > 0 ? ctx.Node.TimeoutSeconds : 300);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
        timeoutCts.CancelAfter(timeout);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return new NodeOutcome.Failed($"command timed out after {timeout}");
        }
        string stdout, stderr;
        try
        {
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return new NodeOutcome.Failed($"command stream read timed out after {timeout}");
        }
        return proc.ExitCode == 0
            ? new NodeOutcome.Succeeded(stdout)
            : new NodeOutcome.Failed($"exit={proc.ExitCode} stderr={stderr}", stdout);
    }
}

public sealed class AINodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.AI;
    private readonly IServiceProvider _sp;

    public AINodeExecutor(IServiceProvider sp) { _sp = sp; }

    public string DescribeInput(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Ai>(ctx.Node.Config);
        var initial = cfg.InitialPrompt ?? "";
        var loopPrompt = cfg.LoopPrompt ?? initial;
        var payload = new
        {
            nodeType = NodeType.ToString(),
            prompt = initial,
            loopPrompt,
            context = new
            {
                workItemTitle = ctx.WorkItem.Title,
                workItemDescription = ctx.WorkItem.Description,
                previousNodeOutput = ctx.PreviousNodeOutput,
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    public async Task<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Ai>(ctx.Node.Config);
        try
        {
            using var scope = _sp.CreateScope();
            var providerStore = scope.ServiceProvider.GetRequiredService<IProviderStore>();
            var provider = await ResolveProviderAsync(providerStore, cfg.AiProviderId);
            if (provider == null) return new NodeOutcome.Failed("No AI provider found");

            var registry = scope.ServiceProvider.GetRequiredService<IAgentAdapterRegistry>();
            var adapter = registry.ResolveForProvider(provider)();

            var executionCount = await CountNodeVisitsAsync(scope.ServiceProvider, ctx.Run.Id, ctx.Node.Id);

            var eventLogService = scope.ServiceProvider.GetRequiredService<IEventLogService>();
            var eventEntries = await eventLogService.GetByRunIdAsync(ctx.Run.Id);
            var eventLogSummary = eventEntries
                .Select(e => $"{e.EventType}: {e.Data}")
                .ToList();

            var runContext = new LoopRunContext(
                ctx.Run.Id,
                ctx.WorkItem.Id,
                ctx.WorkItem.Title,
                ctx.WorkItem.Description ?? "",
                ctx.WorkItem.WorktreePath ?? "",
                ctx.WorkItem.BranchName ?? "",
                eventLogSummary,
                ctx.PreviousNodeOutput);

            var timeout = TimeSpan.FromSeconds(ctx.Node.TimeoutSeconds > 0 ? ctx.Node.TimeoutSeconds : 300);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
            timeoutCts.CancelAfter(timeout);

            var initialPrompt = cfg.InitialPrompt ?? "";
            var loopPrompt = cfg.LoopPrompt ?? initialPrompt;

            var agentCtx = new AgentExecutionContext(
                provider,
                initialPrompt,
                loopPrompt,
                runContext,
                executionCount,
                timeoutCts.Token,
                ctx.ProgressCallback,
                ExtractAdapterConfig(cfg.AdapterConfig));

            var result = await adapter.ExecuteAsync(agentCtx);

            if (result.Success && result.Output != null && !string.IsNullOrEmpty(cfg.RejectPattern))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(result.Output, cfg.RejectPattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    return new NodeOutcome.Failed(
                        "AI rejected: output matched rejectPattern", result.Output);
                }
            }

            return NodeOutcome.FromResult(result);
        }
        catch (OperationCanceledException) { return new NodeOutcome.Failed("AI node timed out"); }
        catch (Exception ex) { return new NodeOutcome.Failed(ex.Message); }
    }

    private static async Task<int> CountNodeVisitsAsync(IServiceProvider sp, Guid runId, Guid nodeId)
    {
        var loopRunStore = sp.GetRequiredService<ILoopRunStore>();
        var runNodes = await loopRunStore.GetRunNodesAsync(runId);
        return runNodes.Count(n => n.LoopNodeId == nodeId);
    }

    private static async Task<AiProvider?> ResolveProviderAsync(IProviderStore store, string? providerKey)
    {
        if (string.IsNullOrEmpty(providerKey))
            return await store.GetDefaultAiProviderAsync();

        if (Guid.TryParse(providerKey, out var id))
            return await store.GetAiProviderByIdAsync(id);

        return await store.GetAiProviderByNameAsync(providerKey);
    }

    private static Dictionary<string, object?>? ExtractAdapterConfig(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } el) return null;
        var result = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Number => prop.Value.GetDouble(),
                JsonValueKind.True or JsonValueKind.False => prop.Value.GetBoolean(),
                JsonValueKind.String => prop.Value.GetString(),
                _ => prop.Value.GetRawText()
            };
        }
        return result;
    }
}

public sealed class HumanNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Human;

    public string DescribeInput(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Human>(ctx.Node.Config);
        return JsonSerializer.Serialize(new { nodeType = NodeType.ToString(), prompt = cfg.Prompt ?? "(no prompt)" });
    }

    public Task<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
        => Task.FromResult<NodeOutcome>(new NodeOutcome.Suspended(
            "Human node awaiting input",
            SuspendKind.HumanInput));
}

public sealed class CleanupNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Cleanup;
    private readonly IRepositoryManager _repo;
    private readonly IServiceProvider _sp;

    public CleanupNodeExecutor(IRepositoryManager repo, IServiceProvider sp)
    {
        _repo = repo;
        _sp = sp;
    }

    public string DescribeInput(NodeExecutionContext ctx)
        => JsonSerializer.Serialize(new { nodeType = NodeType.ToString(), message = "cleanup" });

    public async Task<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        using var scope = _sp.CreateScope();
        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();

        var wi = await workItemStore.GetByIdAsync(ctx.WorkItem.Id);
        if (wi == null) return new NodeOutcome.Failed("WorkItem not found");

        string? worktreePath = wi.WorktreePath;
        if (!string.IsNullOrEmpty(worktreePath) && Directory.Exists(worktreePath))
        {
            try
            {
                await _repo.DestroyWorktreeAsync(worktreePath);
            }
            catch (Exception ex)
            {
                wi.WorktreePath = null;
                await workItemStore.UpdateAsync(wi);
                return new NodeOutcome.Terminal($"cleanup failed: {ex.Message} (path: {worktreePath})");
            }
            wi.WorktreePath = null;
            await workItemStore.UpdateAsync(wi);
            return new NodeOutcome.Terminal($"worktree destroyed: {worktreePath}");
        }

        wi.WorktreePath = null;
        await workItemStore.UpdateAsync(wi);
        return new NodeOutcome.Terminal("cleanup skipped: no worktree");
    }
}
