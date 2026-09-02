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
            return (false, Combined(), ex.Message + KillTree(p));
        }
        catch (Exception ex)
        {
            return (false, Combined(), ex.Message);
        }
        if (p.ExitCode != 0)
            return (false, Combined(), $"exit code {p.ExitCode}");
        return (true, Combined(), null);
    }

    /// <returns>Empty once the tree is gone; otherwise a clause naming why it is not.</returns>
    private static string KillTree(Process p)
    {
        try
        {
            p.Kill(entireProcessTree: true);
            return "";
        }
        catch (InvalidOperationException)
        {
            return "";
        }
        catch (Exception ex)
        {
            return $"; failed to kill the command's process tree: {ex.Message}";
        }
    }
}
