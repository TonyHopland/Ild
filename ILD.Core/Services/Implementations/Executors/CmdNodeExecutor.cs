using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
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
        var loopRunStore = ctx.Services.GetRequiredService<ILoopRunStore>();

        if (string.IsNullOrWhiteSpace(command))
        {
            yield return new NodeOutcome.NodeStarting(null);
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "Cmd node has no command configured");
            yield break;
        }

        var run = await loopRunStore.GetByIdAsync(ctx.Run.Id) ?? ctx.Run;
        var worktree = run.WorktreePath;
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
        var psi = new ProcessStartInfo("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var sb = new StringBuilder();
        var err = new StringBuilder();
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            sb.AppendLine(e.Data);
            try { ctx.ProgressCallback?.Invoke(e.Data); } catch { }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            err.AppendLine(e.Data);
            try { ctx.ProgressCallback?.Invoke(e.Data); } catch { }
        };
        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            return (false, sb.ToString(), ex.Message);
        }
        var combined = sb.ToString() + (err.Length > 0 ? "\n" + err.ToString() : "");
        if (p.ExitCode != 0)
            return (false, combined, $"exit code {p.ExitCode}");
        return (true, combined, null);
    }
}
