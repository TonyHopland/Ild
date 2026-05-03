using System.Diagnostics;
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

    public async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx)
    {
        using var scope = _sp.CreateScope();
        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();
        var providerStore = scope.ServiceProvider.GetRequiredService<IProviderStore>();

        var wi = await workItemStore.GetByIdAsync(ctx.WorkItem.Id);
        if (wi == null) return NodeExecutionResult.Fail("WorkItem not found");
        var repo = await providerStore.GetRepositoryByIdAsync(wi.RepositoryId);
        if (repo == null)
            return NodeExecutionResult.Fail(
                "WorkItem has no repository attached; refusing to run loop without an isolated worktree.");

        if (string.IsNullOrEmpty(wi.WorktreePath) || !Directory.Exists(wi.WorktreePath))
        {
            var branch = wi.BranchName ?? $"ild/wi-{wi.Id:N}";
            var basePath = repo.WorktreesPath;
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
                        return NodeExecutionResult.Fail($"git clone failed: {stderr}");
                }
            }
            var path = await _repo.CreateWorktreeAsync(basePath, branch);
            wi.WorktreePath = path;
            wi.BranchName = branch;
            await workItemStore.UpdateAsync(wi);
        }
        if (string.IsNullOrEmpty(wi.WorktreePath) || !Directory.Exists(wi.WorktreePath))
            return NodeExecutionResult.Fail(
                $"Failed to materialize worktree for WorkItem {wi.Id}; refusing to continue.");
        return NodeExecutionResult.Ok($"worktree={wi.WorktreePath}");
    }
}

public sealed class CmdNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Cmd;

    public async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx)
    {
        var cfg = ctx.Node.Config ?? "{}";
        string command;
        try
        {
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(cfg);
            if (dict == null || !dict.TryGetValue("command", out var c) || c == null)
                return NodeExecutionResult.Fail("Cmd node missing 'command' config");
            command = c.ToString() ?? "";
        }
        catch (Exception ex) { return NodeExecutionResult.Fail($"invalid Cmd config: {ex.Message}"); }

        var cwd = ctx.WorkItem.WorktreePath;
        if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
            return NodeExecutionResult.Fail(
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
            return NodeExecutionResult.Fail($"command timed out after {timeout}");
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
            return NodeExecutionResult.Fail($"command stream read timed out after {timeout}");
        }
        return proc.ExitCode == 0
            ? NodeExecutionResult.Ok(stdout)
            : NodeExecutionResult.Fail($"exit={proc.ExitCode} stderr={stderr}", stdout);
    }
}

public sealed class AINodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.AI;
    private readonly IServiceProvider _sp;

    public AINodeExecutor(IServiceProvider sp) { _sp = sp; }

    public async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx)
    {
        var cfg = ctx.Node.Config ?? "{}";
        try
        {
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(cfg);
            if (dict == null) return NodeExecutionResult.Fail("AI config missing");
            var providerKey = dict.GetValueOrDefault("provider")?.ToString();
            var initialPrompt = dict.GetValueOrDefault("initialPrompt")?.ToString()
                ?? dict.GetValueOrDefault("prompt")?.ToString()
                ?? dict.GetValueOrDefault("promptTemplate")?.ToString() ?? "";
            var loopPrompt = dict.GetValueOrDefault("loopPrompt")?.ToString() ?? initialPrompt;

            using var scope = _sp.CreateScope();
            var providerStore = scope.ServiceProvider.GetRequiredService<IProviderStore>();
            var provider = await ResolveProviderAsync(providerStore, providerKey);
            if (provider == null) return NodeExecutionResult.Fail("No AI provider found");

            var registry = scope.ServiceProvider.GetRequiredService<IAgentAdapterRegistry>();
            var adapter = registry.ResolveForProvider(provider)();

            var executionCount = await CountNodeVisitsAsync(scope.ServiceProvider, ctx.Run.Id, ctx.Node.Id);

            var eventLogService = scope.ServiceProvider.GetRequiredService<IEventLogService>();
            var eventEntries = await eventLogService.GetByRunIdAsync(ctx.Run.Id);
            var eventLogSummary = eventEntries
                .Select(e => $"{e.EventType}: {e.Data}")
                .ToList();

            var runContext = new ILD.Data.DTOs.LoopRunContext(
                ctx.Run.Id,
                ctx.WorkItem.Id,
                ctx.WorkItem.Title,
                ctx.WorkItem.Description ?? "",
                ctx.WorkItem.WorktreePath ?? "",
                ctx.WorkItem.BranchName ?? "",
                eventLogSummary,
                ctx.PreviousNodeOutput);

            var agentCtx = new ILD.Data.DTOs.AgentExecutionContext(
                provider,
                initialPrompt,
                loopPrompt,
                runContext,
                executionCount,
                ctx.CancellationToken,
                ctx.ProgressCallback);

            return await adapter.ExecuteAsync(agentCtx);
        }
        catch (Exception ex) { return NodeExecutionResult.Fail(ex.Message); }
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
            return await store.GetDefaultAiProviderAsync()
                ?? await store.GetFirstAiProviderAsync();

        if (Guid.TryParse(providerKey, out var id))
            return await store.GetAiProviderByIdAsync(id);

        return await store.GetAiProviderByNameAsync(providerKey);
    }
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

    public async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx)
    {
        using var scope = _sp.CreateScope();
        var workItemStore = scope.ServiceProvider.GetRequiredService<IWorkItemStore>();

        var wi = await workItemStore.GetByIdAsync(ctx.WorkItem.Id);
        if (wi == null) return NodeExecutionResult.Fail("WorkItem not found");

        if (!string.IsNullOrEmpty(wi.WorktreePath) && Directory.Exists(wi.WorktreePath))
        {
            try { await _repo.DestroyWorktreeAsync(wi.WorktreePath); } catch { }
        }
        wi.WorktreePath = null;
        await workItemStore.UpdateAsync(wi);
        return NodeExecutionResult.Ok("cleanup complete");
    }
}
