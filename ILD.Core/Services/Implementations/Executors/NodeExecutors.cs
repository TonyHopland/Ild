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
        if (repo == null) return NodeExecutionResult.Ok("no repository attached; skipping worktree");

        if (string.IsNullOrEmpty(wi.WorktreePath) || !Directory.Exists(wi.WorktreePath))
        {
            var branch = wi.BranchName ?? $"ild/wi-{wi.Id:N}";
            var path = await _repo.CreateWorktreeAsync(repo.WorktreesPath ?? Path.GetDirectoryName(repo.CloneUrl) ?? ".", branch);
            wi.WorktreePath = path;
            wi.BranchName = branch;
            await workItemStore.UpdateAsync(wi);
        }
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

        var cwd = ctx.WorkItem.WorktreePath ?? Directory.GetCurrentDirectory();
        if (!Directory.Exists(cwd)) cwd = Directory.GetCurrentDirectory();

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
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var timeout = TimeSpan.FromSeconds(ctx.Node.TimeoutSeconds > 0 ? ctx.Node.TimeoutSeconds : 300);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return NodeExecutionResult.Fail($"command timed out after {timeout}");
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
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
            var provider = dict.GetValueOrDefault("provider")?.ToString() ?? "default";
            var model = dict.GetValueOrDefault("model")?.ToString() ?? "default";
            var prompt = dict.GetValueOrDefault("prompt")?.ToString() ?? "";

            using var scope = _sp.CreateScope();
            var ai = scope.ServiceProvider.GetRequiredService<IAIProviderService>();
            var rendered = await ai.RenderPromptAsync(prompt, new ILD.Data.DTOs.LoopRunContext(
                ctx.Run.Id,
                ctx.WorkItem.Id,
                ctx.WorkItem.Title,
                ctx.WorkItem.Description ?? "",
                ctx.WorkItem.WorktreePath ?? "",
                ctx.WorkItem.BranchName ?? "",
                new List<string>(),
                ctx.PreviousNodeOutput));
            var response = await ai.CompleteAsync(rendered, provider, ctx.CancellationToken);
            return NodeExecutionResult.Ok(response);
        }
        catch (Exception ex) { return NodeExecutionResult.Fail(ex.Message); }
    }
}

public sealed class HumanNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Human;
    public Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx)
        => Task.FromResult(NodeExecutionResult.Ok("waiting human"));
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
        await workItemStore.UpdateAsync(wi);
        return NodeExecutionResult.Ok("cleanup complete");
    }
}
