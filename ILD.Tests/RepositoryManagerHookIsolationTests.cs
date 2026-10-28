using System.Diagnostics;
using System.Runtime.Versioning;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>
/// The attack this change closes: an agent plants a hook, <c>core.fsmonitor</c>, or a
/// <c>.gitattributes</c> filter/textconv driver (defined in the agent-writable
/// <c>.git/config</c>) and waits for the orchestrator to run git over it. The whole
/// commit → diff → rebase → push cycle is run for real over repositories carrying
/// every one of those, with the orchestrator's secrets seeded into the real process
/// environment, and each planted command logs who ran it and with what environment.
///
/// <para>
/// The host cannot <c>setpriv --reuid</c>, so in isolated mode <see cref="AuditingRunner"/>
/// verifies the wrap on every git call and tags the process it then runs; every log
/// entry must come from a process tagged as the agent. The real-uid test below
/// closes the gap on a host that can switch uid and skips elsewhere.
/// </para>
///
/// <para>In <see cref="ProcessGlobalStateCollection"/> because it seeds the process environment.</para>
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
[SupportedOSPlatform("linux")]
public class RepositoryManagerHookIsolationTests : IDisposable
{
    private const string ApiKey = "sentinel-api-key";
    private const string Branch = "ild/wi-20-run-1";

    private static readonly string[] Hooks = ["pre-commit", "pre-push", "post-checkout", "reference-transaction"];

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ild-hook-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _log;
    private readonly string _probe;
    private readonly Dictionary<string, string?> _previousEnvironment = new(StringComparer.Ordinal);
    private readonly List<AuditingRunner> _runners = new();
    private AgentSide _agentSide = new();

    public RepositoryManagerHookIsolationTests()
    {
        Directory.CreateDirectory(_tmp);
        _log = Path.Combine(_tmp, "hook.log");
        File.WriteAllText(_log, string.Empty);
        _probe = Path.Combine(_tmp, "probe.sh");
        File.WriteAllText(_probe,
            "#!/bin/sh\n" +
            "name=\"$1\"\n" +
            $"{{ echo \"ENTRY name=$name route=${{ILD_TEST_ROUTE:-none}} uid=$(id -u)\"; env; echo END; }} >> '{_log}'\n" +
            "case \"$name\" in\n" +
            "  filter) cat ;;\n" +
            "  textconv) cat \"$2\" ;;\n" +
            "  pre-push|reference-transaction) cat > /dev/null ;;\n" +
            "esac\n" +
            "exit 0\n");
        File.SetUnixFileMode(_probe, (UnixFileMode)0b111_101_101);

        foreach (var key in AgentIsolation.SecretEnvironmentKeys) SetEnvironment(key, "sentinel-" + key);
        SetEnvironment("GIT_AUTHOR_NAME", "Seed Author");
        SetEnvironment("GIT_AUTHOR_EMAIL", "author@example.test");
        SetEnvironment("GIT_COMMITTER_NAME", "Seed Committer");
        SetEnvironment("GIT_COMMITTER_EMAIL", "committer@example.test");
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previousEnvironment) Environment.SetEnvironmentVariable(name, value);
        var violations = _runners.SelectMany(r => r.Violations).ToList();
        _agentSide.RemoveOwnFiles(_tmp);
        try { Directory.Delete(_tmp, recursive: true); } catch { }
        GC.SuppressFinalize(this);
        Assert.True(violations.Count == 0, "git isolation violated:\n" + string.Join("\n", violations));
    }

    private void SetEnvironment(string name, string value)
    {
        _previousEnvironment.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    private void Plant(string basePath)
    {
        var gitDir = Path.Combine(basePath, ".git");
        foreach (var hook in Hooks)
            _agentSide.Write(Path.Combine(gitDir, "hooks", hook), $"#!/bin/sh\nexec '{_probe}' {hook} \"$@\"\n", executable: true);

        _agentSide.Git(basePath, "config", "core.fsmonitor", $"{_probe} fsmonitor");
        _agentSide.Git(basePath, "config", "filter.x.clean", $"{_probe} filter");
        _agentSide.Git(basePath, "config", "diff.x.textconv", $"{_probe} textconv");
    }

    /// <summary>Clone, plant, then commit → diff → commit → rebase → push, returning the pushed commit.</summary>
    private async Task<(string Remote, string Head)> RunCycleAsync(RepositoryManager mgr)
    {
        var remote = GitCli.MakeRemote(_tmp, "remote");
        var basePath = Path.Combine(_tmp, "base");
        var auth = new GitAuthOptions(remote, ApiKey, "Forgejo");
        var ct = TestContext.Current.CancellationToken;

        var (cloned, cloneError) = await mgr.CloneAsync(remote, basePath, ct, auth);
        Assert.True(cloned, cloneError);
        Plant(basePath);

        var wt = await mgr.CreateWorktreeAsync(basePath, Branch);
        _agentSide.Write(Path.Combine(wt, ".gitattributes"), "*.txt filter=x diff=x\n");
        _agentSide.Write(Path.Combine(wt, "note.txt"), "first\n");
        Assert.True(await mgr.CommitAsync(wt, "first"));

        _agentSide.Write(Path.Combine(wt, "note.txt"), "second\n");
        var diff = await mgr.GetDiffAsync(wt);
        Assert.Contains("second", diff);
        Assert.True(await mgr.CommitAsync(wt, "second"));

        Assert.True((await mgr.RebaseAsync(wt, "origin/main", ct)).Success);
        var (pushed, pushError) = await mgr.PushAsync(wt, Branch, ct, auth);
        Assert.True(pushed, pushError);

        var head = GitCli.Out(wt, "rev-parse", "HEAD");
        Assert.Equal(head, GitCli.Out(remote, "rev-parse", "refs/heads/" + Branch));
        Assert.Equal("Seed Author", GitCli.Out(remote, "log", "-1", "--format=%an", Branch));
        return (remote, head);
    }

    private List<(string Name, string Route, string Uid, string Body)> Entries()
    {
        var entries = new List<(string, string, string, string)>();
        foreach (var chunk in File.ReadAllText(_log).Split("ENTRY ", StringSplitOptions.RemoveEmptyEntries))
        {
            var first = chunk.Split('\n', 2)[0];
            string Field(string key) => first.Split(' ').First(p => p.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..];
            entries.Add((Field("name"), Field("route"), Field("uid"), chunk));
        }
        return entries;
    }

    private void AssertNothingSecretReachedAPlantedCommand(IReadOnlyList<(string Name, string Route, string Uid, string Body)> entries)
    {
        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            foreach (var key in AgentIsolation.SecretEnvironmentKeys)
                Assert.DoesNotContain("sentinel-" + key, entry.Body);
            Assert.DoesNotContain(ApiKey, entry.Body);
            Assert.DoesNotContain("ILD_GIT_PASSWORD", entry.Body);
            Assert.DoesNotContain("ILD_GIT_USERNAME", entry.Body);
            Assert.DoesNotContain("GIT_ASKPASS", entry.Body);
        }

        // The hooks are the repository's own: they keep running, as the agent.
        var ran = entries.Select(e => e.Name).ToHashSet();
        Assert.Contains("pre-commit", ran);
        Assert.Contains("filter", ran);
        Assert.Contains("textconv", ran);
        Assert.Contains("pre-push", ran);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Planted_hooks_filters_and_fsmonitor_only_ever_run_as_the_agent_with_no_secrets(bool isolated)
    {
        var runner = new AuditingRunner(isolated, tagRoutes: true);
        _runners.Add(runner);
        runner.Forbid(Path.Combine(_tmp, "base"));
        runner.Forbid(Path.Combine(_tmp, "wt"));
        var mgr = new RepositoryManager(runner, null, Path.Combine(_tmp, "wt"), runner.ManagerAgent, Path.Combine(_tmp, "mirrors"), sharedGroup: "agents");

        await RunCycleAsync(mgr);

        var entries = Entries();
        AssertNothingSecretReachedAPlantedCommand(entries);
        Assert.All(entries, e => Assert.Equal("agent", e.Route));
        runner.AssertClean();
    }

    [Fact]
    public async Task Planted_commands_run_under_the_agent_uid_not_the_orchestrators()
    {
        var user = Environment.GetEnvironmentVariable("ILD_TEST_AGENT_USER") ?? "agent";
        var group = Environment.GetEnvironmentVariable("ILD_TEST_AGENT_GROUP") ?? "agent";
        var sharedGroup = Environment.GetEnvironmentVariable("ILD_TEST_SHARED_GROUP") ?? "ild-agents";
        var home = Environment.GetEnvironmentVariable("ILD_TEST_AGENT_HOME") ?? "/home/agent";
        var ownUid = Capture("id", "-u");
        var agentUid = TryCapture("/usr/bin/setpriv", $"--reuid={user}", $"--regid={group}", "--init-groups", "id", "-u");
        if (!OperatingSystem.IsLinux() || agentUid is null || agentUid == ownUid)
            Assert.Skip($"this host cannot switch to the agent user '{user}'; run in the container with ILD_TEST_AGENT_USER/GROUP/HOME set if they differ");

        foreach (var dir in new[] { _tmp })
            File.SetUnixFileMode(dir, (UnixFileMode)0b111_111_111);
        File.SetUnixFileMode(_log, (UnixFileMode)0b110_110_110);
        File.SetUnixFileMode(_probe, (UnixFileMode)0b111_101_101);
        var worktrees = Directory.CreateDirectory(Path.Combine(_tmp, "wt")).FullName;
        File.SetUnixFileMode(worktrees, (UnixFileMode)0b111_111_111);
        var mgr = new RepositoryManager(new ProcessRunner(), null, worktrees, new AgentIdentity(user, group, home), Path.Combine(_tmp, "mirrors"), sharedGroup);
        // The agent owns the clone and the worktree, and in a real attack it is the agent that plants.
        _agentSide = new AsAgent(user, group, home);

        await RunCycleAsync(mgr);

        var entries = Entries();
        AssertNothingSecretReachedAPlantedCommand(entries);
        Assert.All(entries, e => Assert.Equal(agentUid, e.Uid));
        Assert.All(entries, e => Assert.NotEqual(ownUid, e.Uid));
    }

    /// <summary>The writes an attacker makes in the agent's repositories; here the test process makes them directly.</summary>
    private class AgentSide
    {
        public virtual void Write(string path, string content, bool executable = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            if (executable) File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        }

        public virtual void Git(string cwd, params string[] args) => GitCli.Run(cwd, args);

        public virtual void RemoveOwnFiles(string root) { }
    }

    /// <summary>Makes those writes as the agent user, which owns the clone and the worktree they go into.</summary>
    private sealed class AsAgent(string user, string group, string home) : AgentSide
    {
        public override void Write(string path, string content, bool executable = false)
            => Run("mkdir -p -- \"$(dirname -- \"$1\")\" && printf %s \"$2\" > \"$1\" && { [ \"$3\" = 0 ] || chmod 755 -- \"$1\"; }",
                path, content, executable ? "1" : "0");

        public override void Git(string cwd, params string[] args)
            => Run("cd -- \"$1\" && shift && exec git \"$@\"", [cwd, .. args]);

        /// <summary>The test process cannot delete what the agent made in its own folders, so the agent removes it first.</summary>
        public override void RemoveOwnFiles(string root)
        {
            try { Run("find \"$1\" -mindepth 1 -user \"$(id -u)\" -prune -exec rm -rf -- {} +", root); } catch { }
        }

        private void Run(string script, params string[] args)
        {
            var psi = new ProcessStartInfo("/usr/bin/setpriv") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { $"--reuid={user}", $"--regid={group}", "--init-groups", "--", "sh", "-c", script, "sh" }.Concat(args))
                psi.ArgumentList.Add(a);
            psi.Environment["HOME"] = home;
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"as {user}: sh -c '{script}' {string.Join(' ', args)} exited {p.ExitCode}: {stderr}");
        }
    }

    private static string Capture(string file, params string[] args)
        => TryCapture(file, args) ?? throw new InvalidOperationException($"{file} failed");

    private static string? TryCapture(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
