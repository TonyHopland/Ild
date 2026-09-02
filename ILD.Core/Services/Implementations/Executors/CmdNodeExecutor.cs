using ILD.Data.Enums;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Text;

namespace ILD.Core.Services.Implementations.Executors;

public sealed class CmdNodeExecutor : INodeExecutor
{
    public NodeType NodeType => NodeType.Cmd;

    public async IAsyncEnumerable<NodeOutcome> ExecuteAsync(NodeExecutionContext ctx)
    {
        var cfg = NodeConfig.Parse<NodeConfig.Cmd>(ctx.Node.Config);
        var command = cfg.Command;
        var workItems = ctx.Services.GetRequiredService<IWorkItemManager>();

        if (string.IsNullOrWhiteSpace(command))
        {
            yield return new NodeOutcome.NodeStarting(null);
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "Cmd node has no command configured");
            yield break;
        }

        var worktree = ctx.Run.WorktreePath;
        if (string.IsNullOrEmpty(worktree) || !Directory.Exists(worktree))
        {
            yield return new NodeOutcome.NodeStarting(command);
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "No worktree available for Cmd node");
            yield break;
        }

        yield return new NodeOutcome.NodeStarting(command);

        var (ok, output, error) = await RunProcessAsync(command, worktree, ctx);
        if (!ok)
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, error ?? "command failed", output);
            yield break;
        }
        yield return new NodeOutcome.Success(EdgeType.OnSuccess, output);
    }

    private static async Task<(bool Ok, string Output, string? Error)> RunProcessAsync(
        string command, string workingDirectory, NodeExecutionContext ctx)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        var sb = new StringBuilder();
        var err = new StringBuilder();
        // A loop-authored command, run as the orchestrator against a worktree the
        // agent just wrote: it must not inherit the orchestrator's ambient
        // capabilities. Same wrap as git/npm and the AI provider's shell tool
        // (ADR-0014).
        using var p = new Process
        {
            StartInfo = AgentIsolation.DropInheritedCapabilities(psi),
            EnableRaisingEvents = true,
        };
        // Forward the full stdout+stderr stream verbatim (newline included, ANSI
        // preserved) so the live view captures the complete output rather than
        // newline-stripped fragments.
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            sb.AppendLine(e.Data);
            try { ctx.ProgressCallback?.Invoke(e.Data + "\n"); } catch { }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            err.AppendLine(e.Data);
            try { ctx.ProgressCallback?.Invoke(e.Data + "\n"); } catch { }
        };
        string Combined() => sb.ToString() + (err.Length > 0 ? "\n" + err.ToString() : "");
        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ctx.CancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            // Waiting stops; the shell does not. Without this reap a cancelled or
            // timed-out node leaves /bin/sh and its children running over the
            // worktree the engine is about to commit and delete. The tree is the
            // orchestrator's own — the wrap above changes no uid — so the kill
            // needs no privilege and a failure means something worth reading, not
            // something to swallow.
            var reaped = await ReapAsync(p);
            return (false, Combined(), ex.Message + reaped);
        }
        catch (Exception ex)
        {
            return (false, Combined(), ex.Message);
        }
        if (p.ExitCode != 0)
            return (false, Combined(), $"exit code {p.ExitCode}");
        return (true, Combined(), null);
    }

    // Long enough that only a tree genuinely stuck in the kernel — an
    // uninterruptible write to a slow mount — reaches the timeout, short enough
    // that a run being cancelled still ends promptly.
    private static readonly TimeSpan ReapGrace = TimeSpan.FromSeconds(5);

    /// <returns>Empty once the tree is gone; otherwise a clause naming why it is not.</returns>
    private static async Task<string> ReapAsync(Process p)
    {
        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            return "";
        }
        catch (Exception ex)
        {
            return $"; failed to kill the command's process tree: {ex.Message}";
        }

        // Kill only signals. Returning before the tree is actually gone would hand
        // the caller a node that reads finished while the command is still holding
        // the worktree it is about to commit and delete, so wait for the exit that
        // makes "finished" true.
        using var grace = new CancellationTokenSource(ReapGrace);
        try
        {
            await p.WaitForExitAsync(grace.Token);
            return "";
        }
        catch (OperationCanceledException)
        {
            return $"; the command's process tree was still alive {ReapGrace.TotalSeconds:0}s after being killed";
        }
    }
}
