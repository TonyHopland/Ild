using ILD.Core.Services.RunHosts.Local;
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
/// <see cref="AuditingRunner"/> checks every git call and tags the process it then runs;
/// every log entry must come from a process tagged as agent-scoped.
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
    private readonly AgentSide _agentSide = new();

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
        Assert.True(await mgr.HasUncommittedChangesAsync(wt));
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

        // The hooks are the repository's own: they keep running, agent-scoped.
        var ran = entries.Select(e => e.Name).ToHashSet();
        Assert.Contains("pre-commit", ran);
        Assert.Contains("filter", ran);
        // No textconv: ILD asks only whether the tree differs and never reads the diff, so nothing converts it.
        Assert.DoesNotContain("textconv", ran);
        Assert.Contains("pre-push", ran);
    }

    [Fact]
    public async Task Planted_hooks_filters_and_fsmonitor_only_ever_run_agent_scoped_with_no_secrets()
    {
        var runner = new AuditingRunner(tagRoutes: true);
        _runners.Add(runner);
        runner.Forbid(Path.Combine(_tmp, "base"));
        runner.Forbid(Path.Combine(_tmp, "wt"));
        var mgr = new RepositoryManager(runner, new LocalRunHost(runner), null, Path.Combine(_tmp, "wt"), Path.Combine(_tmp, "mirrors"));

        await RunCycleAsync(mgr);

        var entries = Entries();
        AssertNothingSecretReachedAPlantedCommand(entries);
        Assert.All(entries, e => Assert.Equal("agent", e.Route));
        runner.AssertClean();
    }

    /// <summary>The writes an attacker makes in the agent's repositories; here the test process makes them directly.</summary>
    private sealed class AgentSide
    {
        public void Write(string path, string content, bool executable = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            if (executable) File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        }

        public void Git(string cwd, params string[] args) => GitCli.Run(cwd, args);
    }
}
