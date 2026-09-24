using System.Diagnostics;
using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

public class CmdNodeExecutorTests : IDisposable
{
    private readonly string _worktree;

    public CmdNodeExecutorTests()
    {
        _worktree = Path.Combine(Path.GetTempPath(), "ild-cmd-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_worktree);
    }

    public void Dispose()
    {
        try { Directory.Delete(_worktree, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private NodeExecutionContext Context(
        string command, CancellationToken cancel = default, Func<string, Task>? progress = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IWorkItemManager>().Object);
        return new NodeExecutionContext(
            new LoopRun { Id = Guid.NewGuid(), WorktreePath = _worktree },
            new LoopNode
            {
                Id = Guid.NewGuid(),
                NodeType = NodeType.Cmd,
                Label = "cmd",
                Config = System.Text.Json.JsonSerializer.Serialize(new { command }),
            },
            services.BuildServiceProvider(),
            cancel,
            progress);
    }

    private static async Task<List<NodeOutcome>> RunAsync(NodeExecutionContext ctx)
    {
        var outcomes = new List<NodeOutcome>();
        await foreach (var outcome in new CmdNodeExecutor().ExecuteAsync(ctx)) outcomes.Add(outcome);
        return outcomes;
    }

    [Theory]
    // The command reaches /bin/sh as one argv entry, so the shell — not .NET's
    // argument parser, and not a hand-rolled escape — is what interprets it.
    [InlineData("echo 'it'\\''s \"quoted\"'", "it's \"quoted\"")]
    [InlineData("printf '%s\\n' 'C:\\path\\to\\thing'", "C:\\path\\to\\thing")]
    [InlineData("echo \\\\", "\\")]
    [InlineData("echo \"a  b\"", "a  b")]
    // A trailing backslash and a backslash before a closing quote are what the
    // old hand-rolled escaping mangled: it doubled quotes into the argument
    // string and .NET's parser then ate the backslash guarding them.
    [InlineData("echo \"x\\\\\"", "x\\")]
    [InlineData("echo hi \\", "hi \\")]
    public async Task A_command_is_interpreted_by_the_shell_verbatim(string command, string expected)
    {
        var outcomes = await RunAsync(Context(command));

        var success = Assert.IsType<NodeOutcome.Success>(Assert.Single(outcomes, o => o is NodeOutcome.Success));
        Assert.Equal(expected, success.Output?.TrimEnd('\n'));
    }

    [Fact]
    public async Task A_failing_command_fails_the_node_with_its_output()
    {
        var outcomes = await RunAsync(Context("echo nope; echo bad >&2; exit 3"));

        var fail = Assert.IsType<NodeOutcome.Fail>(Assert.Single(outcomes, o => o is NodeOutcome.Fail));
        Assert.Equal("exit code 3", fail.Reason);
        Assert.Contains("nope", fail.Output);
        Assert.Contains("bad", fail.Output);
    }

    [Fact]
    public async Task Cancelling_reaps_the_whole_process_tree()
    {
        // The node's own /bin/sh is not the risk — a background grandchild is: it
        // outlives a kill that only reaches the direct child, and keeps writing the
        // worktree the engine is about to commit and delete. This one records its
        // pid before the node announces itself, then would write a marker once its
        // sleep ends; the marker is the orphan.
        var survivor = Path.Combine(_worktree, "survivor.txt");
        var pidFile = Path.Combine(_worktree, "gc.pid");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();

        var ctx = Context(
            $"sh -c 'echo $$ > \"{pidFile}\"; sleep 30; echo alive > \"{survivor}\"' & " +
            $"while [ ! -s '{pidFile}' ]; do :; done; echo started; sleep 30",
            cancel.Token,
            line =>
            {
                if (line.Contains("started", StringComparison.Ordinal)) started.TrySetResult();
                return Task.CompletedTask;
            });

        var run = RunAsync(ctx);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var grandchild = int.Parse(File.ReadAllText(pidFile).Trim());
        cancel.Cancel();

        var outcomes = await run.WaitAsync(TimeSpan.FromSeconds(30));
        var fail = Assert.IsType<NodeOutcome.Fail>(Assert.Single(outcomes, o => o is NodeOutcome.Fail));

        // The node does not report finished until the shell has actually exited,
        // so the caller can delete the worktree the moment it sees this outcome.
        Assert.DoesNotContain("still alive", fail.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("failed to kill", fail.Reason, StringComparison.Ordinal);

        Assert.True(await HasExitedAsync(grandchild), "a background child of the cancelled command survived the node");
        Assert.False(File.Exists(survivor), "a background child of the cancelled command survived the node");
    }

    // A Cmd command runs in a worktree the agent just wrote, so in practice it
    // runs agent-authored code and gets exactly the agent's privileges
    // (ADR-0014/0016). Driven through the explicit-parameter seams: the test
    // process is always single-uid (TestEnvironmentBaseline).
    private const string TrickyCommand = "echo \"a  b\" 'it'\\''s' $HOME \\";

    private static IEnumerable<string> OrchestratorOnlyNames() =>
        AgentIsolation.SecretEnvironmentKeys.Concat(AgentIsolation.OrchestratorTopologyEnvKeys).Distinct();

    [Fact]
    public void Under_uid_isolation_the_command_crosses_to_the_agent_uid_intact_in_the_worktree()
    {
        var psi = CmdNodeExecutor.ShellStartInfo(TrickyCommand, _worktree);
        psi.Environment["HOME"] = "/home/ild";

        var spawned = CmdNodeExecutor.IsolateCommand(psi, "agent", "agents", "/home/agent", null);

        Assert.Equal("/usr/bin/setpriv", spawned.FileName);
        var args = spawned.ArgumentList.ToList();
        var sh = args.IndexOf("/bin/sh");
        Assert.True(sh > 0, "the shell must be the command setpriv runs: " + string.Join(' ', args));
        var setprivArgs = args.Take(sh).ToList();
        Assert.Contains("--reuid=agent", setprivArgs);
        Assert.Contains("--regid=agents", setprivArgs);
        Assert.Contains("--init-groups", setprivArgs);
        Assert.Contains("--inh-caps=-all", setprivArgs);
        Assert.Contains("--ambient-caps=-all", setprivArgs);
        Assert.Equal(new[] { "/bin/sh", "-c", TrickyCommand }, args.Skip(sh));
        Assert.Equal(_worktree, spawned.WorkingDirectory);
        Assert.Equal("/home/agent", spawned.Environment["HOME"]);
    }

    [Theory]
    [InlineData("agent")]
    // Single-uid mode keeps the orchestrator uid but still sheds what the command
    // has no use for, as the AI provider's shell tool does.
    [InlineData(null)]
    public void The_command_inherits_none_of_the_orchestrators_secrets_or_topology(string? agentUser)
    {
        var psi = CmdNodeExecutor.ShellStartInfo("echo hi", _worktree);
        var seeded = OrchestratorOnlyNames().ToList();
        foreach (var name in seeded)
            psi.Environment[name] = "orchestrator-only";
        // Seeded for real, so the absence checked below is the strip's doing.
        Assert.All(seeded, name => Assert.True(psi.Environment.ContainsKey(name)));
        Assert.Contains("ILD_SECRET_KEY", seeded);
        Assert.Contains("ILD_DB_CONNECTION_STRING", seeded);
        Assert.Contains("WORKITEM_DB_CONNECTION_STRING", seeded);
        Assert.Contains("ILD_SESSION_TOKEN_PEPPER", seeded);
        Assert.Contains("ILD_API_TOKEN", seeded);
        Assert.Contains("ILD_AGENT_TOKEN", seeded);
        Assert.Contains(AgentIsolation.PrivateRootEnvVar, seeded);
        psi.Environment["GIT_AUTHOR_NAME"] = "ILD Agent";
        psi.Environment["PATH"] = "/usr/bin:/bin";

        var spawned = CmdNodeExecutor.IsolateCommand(psi, agentUser, agentUser, "/home/agent", null);

        var leaked = seeded.Where(spawned.Environment.ContainsKey).ToList();
        Assert.True(leaked.Count == 0, "leaked to the Cmd node's shell: " + string.Join(", ", leaked));
        Assert.Equal("ILD Agent", spawned.Environment["GIT_AUTHOR_NAME"]);
        Assert.Equal("/usr/bin:/bin", spawned.Environment["PATH"]);
    }

    [Fact]
    public void The_command_carries_the_egress_proxy_over_a_stale_inherited_one()
    {
        const string proxy = "http://127.0.0.1:1";
        var psi = CmdNodeExecutor.ShellStartInfo("echo hi", _worktree);
        foreach (var (key, _) in AgentIsolation.EgressProxyEnvironment(proxy))
            psi.Environment[key] = "http://stale.invalid:9";

        var spawned = CmdNodeExecutor.IsolateCommand(psi, "agent", "agent", "/home/agent", proxy);

        foreach (var (key, value) in AgentIsolation.EgressProxyEnvironment(proxy))
            Assert.Equal(value, spawned.Environment[key]);
        Assert.Equal(proxy, spawned.Environment["HTTP_PROXY"]);
    }

    [Fact]
    public void Single_uid_mode_spawns_a_plain_shell_in_the_worktree_with_path_and_home_as_inherited()
    {
        var psi = CmdNodeExecutor.ShellStartInfo(TrickyCommand, _worktree);
        psi.Environment["PATH"] = "/usr/bin:/bin";
        psi.Environment["HOME"] = "/home/ild";

        var spawned = CmdNodeExecutor.IsolateCommand(psi, null, null, "/home/agent", null);

        Assert.Equal("/bin/sh", spawned.FileName);
        Assert.Equal(new[] { "-c", TrickyCommand }, spawned.ArgumentList);
        Assert.Equal(_worktree, spawned.WorkingDirectory);
        Assert.Equal("/usr/bin:/bin", spawned.Environment["PATH"]);
        Assert.Equal("/home/ild", spawned.Environment["HOME"]);
    }

    [Fact]
    public async Task A_real_run_does_not_hand_the_command_the_orchestrators_environment()
    {
        // The seams above are only half of it: the executor must actually spawn
        // through them. The test process carries a real orchestrator-private root
        // (TestEnvironmentBaseline), so a run that inherits it shows it in `env`.
        var inherited = Environment.GetEnvironmentVariable(AgentIsolation.PrivateRootEnvVar);
        Assert.False(string.IsNullOrEmpty(inherited), "the probe variable must be set for this test to mean anything");

        var outcomes = await RunAsync(Context("env"));

        var success = Assert.IsType<NodeOutcome.Success>(Assert.Single(outcomes, o => o is NodeOutcome.Success));
        Assert.Contains("PATH=", success.Output);
        Assert.DoesNotContain(AgentIsolation.PrivateRootEnvVar + "=", success.Output);
    }

    [Fact]
    public async Task Setpriv_execs_the_shell_in_place_so_the_held_process_is_the_tree_root()
    {
        // The reap kills p's tree. Under uid isolation p is started as setpriv, so
        // that reaches the command only if setpriv becomes the shell rather than
        // forking it. The caps-only wrap is the same setpriv minus the uid switch,
        // which this process has no privilege for.
        var psi = AgentIsolation.DropInheritedCapabilities(
            CmdNodeExecutor.ShellStartInfo("echo $$", _worktree), agentUser: "agent");
        Assert.Equal("/usr/bin/setpriv", psi.FileName);

        using var p = Process.Start(psi)!;
        var printed = (await p.StandardOutput.ReadToEndAsync()).Trim();
        await p.WaitForExitAsync();

        Assert.Equal(0, p.ExitCode);
        Assert.Equal(p.Id.ToString(), printed);
    }

    [Fact]
    public void The_cmd_spawn_routes_to_the_agent_uid_rather_than_only_dropping_caps()
    {
        var source = Regex.Replace(
            File.ReadAllText(Path.Combine(
                RepositoryRoot(), "ILD.Core", "Services", "Implementations", "Executors", "CmdNodeExecutor.cs")),
            @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);

        Assert.DoesNotContain("AgentIsolation.DropInheritedCapabilities(", source);
        Assert.Contains("AgentIsolation.StripOrchestratorEnvironment(", source);
        Assert.Contains("AgentIsolation.Route(", source);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ILD.sln"))) return dir.FullName;
        throw new InvalidOperationException("ILD.sln not found above " + AppContext.BaseDirectory);
    }

    // The grandchild is not our child, so there is no exit to await: poll /proc
    // until it is gone or a zombie. The deadline is only the failure path.
    private static async Task<bool> HasExitedAsync(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            string stat;
            try { stat = File.ReadAllText($"/proc/{pid}/stat"); }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }

            // The state follows the parenthesised command name, which may itself
            // contain spaces or parentheses.
            if (stat[(stat.LastIndexOf(')') + 2)..].StartsWith('Z')) return true;
            await Task.Delay(20);
        }

        return false;
    }
}
