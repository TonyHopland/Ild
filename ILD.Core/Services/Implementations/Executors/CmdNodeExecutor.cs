using ILD.Core.Services.RunHosts.Local;
using ILD.Data.Enums;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        var host = ctx.Services.GetRunHost();
        var worktree = ctx.Run.WorktreePath;
        if (string.IsNullOrEmpty(worktree) || !await host.DirectoryExistsAsync(worktree, ctx.CancellationToken))
        {
            yield return new NodeOutcome.NodeStarting(command);
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, "No worktree available for Cmd node");
            yield break;
        }

        yield return new NodeOutcome.NodeStarting(command);

        var feeds = await ctx.Services.GetRequiredService<IPackageFeedResolver>()
            .ResolveAsync(await RunRepository.IdOfAsync(ctx.Run, workItems), ctx.CancellationToken);
        var (ok, output, error) = await RunProcessAsync(host, command, worktree, feeds.Feeds, ctx);
        if (!ok)
        {
            yield return new NodeOutcome.Fail(EdgeType.OnFailure, error ?? "command failed", output);
            yield break;
        }
        yield return new NodeOutcome.Success(EdgeType.OnSuccess, output);
    }

    private static async Task<(bool Ok, string Output, string? Error)> RunProcessAsync(
        IRunHost host, string command, string workingDirectory, IReadOnlyList<PackageFeedCredential> packageFeeds, NodeExecutionContext ctx)
    {
        var sb = new StringBuilder();
        var err = new StringBuilder();
        using var feeds = PackageFeedCredentialFiles.Materialize(
            packageFeeds, ctx.Run.Id.ToString("N"), ctx.Services.GetService<ILogger<CmdNodeExecutor>>());
        var mode = await ctx.Services.GetService<IEgressPolicy>().LaunchModeAsync(ctx.CancellationToken);
        var psi = IsolateCommand(ShellStartInfo(command, workingDirectory), AgentIsolation.LaunchProxy(mode, aiProviderId: null));
        foreach (var (name, value) in feeds.Environment)
            psi.Environment[name] = value;
        // The pumps append to each buffer under its own lock, so a read takes both.
        string Combined()
        {
            lock (sb)
            lock (err)
                return sb.ToString() + (err.Length > 0 ? "\n" + err.ToString() : "");
        }

        IRunProcess p;
        try
        {
            p = await host.StartCommandAsync(
                RunCommand.Prepared(psi, ctx.Run.Id, feeds.Environment, feeds.FilePath is null ? null : [feeds.FilePath]), ctx.CancellationToken);
        }
        catch (Exception ex)
        {
            return (false, Combined(), ex.Message);
        }

        using var _ = p;
        // Forward the full stdout+stderr stream verbatim (newline included, ANSI
        // preserved) so the live view captures the complete output rather than
        // newline-stripped fragments.
        var readers = Task.WhenAll(
            PumpAsync(p.StandardOutput, sb, ctx.ProgressCallback),
            PumpAsync(p.StandardError, err, ctx.ProgressCallback));
        try
        {
            await p.WaitForExitAsync(ctx.CancellationToken);
            await readers.WaitAsync(ctx.CancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            // Waiting stops; the shell does not. Without this reap a cancelled or
            // timed-out node leaves /bin/sh and its children running over the
            // worktree the engine is about to commit and delete. A failed kill
            // means something worth reading, not something to swallow.
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

    private static async Task PumpAsync(StreamReader reader, StringBuilder into, Func<string, Task>? progress)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (into) into.AppendLine(line);
            try { progress?.Invoke(line + "\n"); } catch { }
        }
    }

    internal static ProcessStartInfo ShellStartInfo(string command, string workingDirectory)
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
        return psi;
    }

    /// <summary>
    /// The command is loop-authored, but it runs in a worktree the agent just
    /// wrote — its builds, test suites and scripts — so it gets what an agent
    /// launch gets (ADR-0016): none of the orchestrator's secrets, and the egress
    /// proxy of the network mode.
    /// </summary>
    internal static ProcessStartInfo IsolateCommand(ProcessStartInfo psi, string? egressProxy)
        => AgentIsolation.ApplyLaunchEnvironment(AgentIsolation.StripOrchestratorEnvironment(psi), egressProxy);

    // Long enough that only a tree genuinely stuck in the kernel — an
    // uninterruptible write to a slow mount — reaches the timeout, short enough
    // that a run being cancelled still ends promptly.
    private static readonly TimeSpan ReapGrace = TimeSpan.FromSeconds(5);

    /// <returns>Empty once the tree is gone; otherwise a clause naming why it is not.</returns>
    private static async Task<string> ReapAsync(IRunProcess p)
    {
        if (!p.TryKill(out var failure))
            return $"; failed to kill the command's process tree: {failure}";

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
