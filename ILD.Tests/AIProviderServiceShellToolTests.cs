using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The built-in provider's shell tool runs a command string the model wrote, so
/// it crosses to the agent uid with the orchestrator environment stripped
/// (ADR-0014/0016), and its file tools stay inside the worktree.
/// </summary>
public class AIProviderServiceShellToolTests : IDisposable
{
    private readonly string _tmp = Directory.CreateTempSubdirectory("ild-shell-tool-").FullName;

    public void Dispose() => Directory.Delete(_tmp, recursive: true);

    private static AIProviderService Service() =>
        new(Mock.Of<ILD.Data.Stores.Interfaces.IProviderStore>(), Mock.Of<IWorkItemManager>(),
            Mock.Of<IWorktreePreviewService>(), new HttpClient());

    private static IEnumerable<string> OrchestratorOnlyNames() =>
        AgentIsolation.SecretEnvironmentKeys.Concat(AgentIsolation.OrchestratorTopologyEnvKeys).Distinct();

    [Fact]
    public void Model_authored_command_crosses_to_the_agent_uid_rather_than_only_dropping_caps()
    {
        var psi = AIProviderService.ShellStartInfo("echo hi", _tmp);

        var spawned = AIProviderService.IsolateShell(psi, "agent", "agent", "/home/agent", null);

        Assert.Equal("/usr/bin/setpriv", spawned.FileName);
        var args = spawned.ArgumentList.ToList();
        Assert.Contains("--reuid=agent", args);
        Assert.Contains("--regid=agent", args);
        Assert.Contains("--inh-caps=-all", args);
        Assert.Contains("--ambient-caps=-all", args);
        var sh = args.IndexOf("/bin/sh");
        Assert.True(sh > 0, "the shell must be the command setpriv runs: " + string.Join(' ', args));
        Assert.Equal(new[] { "/bin/sh", "-c", "echo hi" }, args.Skip(sh));
        Assert.True(args.IndexOf("--reuid=agent") < sh);
        Assert.Equal(_tmp, spawned.WorkingDirectory);
    }

    [Fact]
    public void Model_authored_command_does_not_inherit_orchestrator_secrets_or_topology()
    {
        var psi = AIProviderService.ShellStartInfo("echo hi", _tmp);
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
        psi.Environment["GIT_AUTHOR_NAME"] = "ILD Agent";

        var spawned = AIProviderService.IsolateShell(psi, "agent", "agent", "/home/agent", null);

        var leaked = seeded.Where(spawned.Environment.ContainsKey).ToList();
        Assert.True(leaked.Count == 0, "leaked to the model's shell: " + string.Join(", ", leaked));
        Assert.Equal("ILD Agent", spawned.Environment["GIT_AUTHOR_NAME"]);
    }

    [Fact]
    public void Model_authored_command_carries_the_egress_proxy_when_one_is_in_effect()
    {
        const string proxy = "http://127.0.0.1:1";
        var psi = AIProviderService.ShellStartInfo("echo hi", _tmp);
        psi.Environment["HTTP_PROXY"] = "http://stale.invalid:9";

        var spawned = AIProviderService.IsolateShell(psi, "agent", "agent", "/home/agent", proxy);

        foreach (var (key, value) in AgentIsolation.EgressProxyEnvironment(proxy))
            Assert.Equal(value, spawned.Environment[key]);
        Assert.Equal(proxy, spawned.Environment["HTTP_PROXY"]);
    }

    [Fact]
    public void Single_uid_mode_spawns_a_plain_shell_in_the_worktree()
    {
        var psi = AIProviderService.ShellStartInfo("echo hi", _tmp);

        var spawned = AIProviderService.IsolateShell(psi, null, null, null, null);

        Assert.Equal("/bin/sh", spawned.FileName);
        Assert.Equal(new[] { "-c", "echo hi" }, spawned.ArgumentList);
        Assert.Equal(_tmp, spawned.WorkingDirectory);
        Assert.DoesNotContain(spawned.ArgumentList, a => a.Contains("setpriv", StringComparison.Ordinal));
    }

    [Fact]
    public void Single_uid_mode_still_strips_orchestrator_secrets_and_topology()
    {
        var secret = AgentIsolation.SecretEnvironmentKeys.First();
        var topology = AgentIsolation.OrchestratorTopologyEnvKeys.First();
        var psi = AIProviderService.ShellStartInfo("echo hi", _tmp);
        psi.Environment[secret] = "orchestrator-only";
        psi.Environment[topology] = "orchestrator-only";
        psi.Environment["PATH"] = "/usr/bin:/bin";
        psi.Environment["HOME"] = "/home/ild";

        var spawned = AIProviderService.IsolateShell(psi, null, null, null, null);

        Assert.False(spawned.Environment.ContainsKey(secret));
        Assert.False(spawned.Environment.ContainsKey(topology));
        Assert.Equal("/bin/sh", spawned.FileName);
        Assert.Equal(new[] { "-c", "echo hi" }, spawned.ArgumentList);
        Assert.Equal("/usr/bin:/bin", spawned.Environment["PATH"]);
        Assert.Equal("/home/ild", spawned.Environment["HOME"]);
    }

    [Fact]
    public async Task Shell_exec_returns_stdout_and_success()
    {
        var result = await Service().ExecuteToolAsync("shell.exec", "echo hi", _tmp);

        Assert.True(result.Success, result.Error);
        Assert.Equal("hi", result.Output.Trim());
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Shell_exec_runs_in_the_worktree()
    {
        await File.WriteAllTextAsync(Path.Combine(_tmp, "marker.txt"), "here");

        var result = await Service().ExecuteToolAsync("shell.exec", "cat marker.txt", _tmp);

        Assert.True(result.Success, result.Error);
        Assert.Equal("here", result.Output);
    }

    [Fact]
    public async Task Shell_exec_reports_stderr_and_exit_code_on_failure()
    {
        var result = await Service().ExecuteToolAsync("shell.exec", "echo boom >&2; exit 3", _tmp);

        Assert.False(result.Success);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("boom", result.Error?.Trim());
    }

    [Fact]
    public void Shell_exec_and_git_diff_share_one_isolated_spawn()
    {
        // git.diff's merge-base, add --intent-to-add and diff shells must get the
        // same crossing as shell.exec. One spawn site in the service means they
        // cannot take a different path; that site must route rather than only
        // shed caps as the orchestrator.
        var source = StripComments(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "ILD.Core", "Services", "Implementations", "AIProviderService.cs")));

        Assert.Single(Regex.Matches(source, @"\bProcess\.Start\s*\(|\bnew\s+Process\b"));
        Assert.DoesNotContain("AgentIsolation.DropInheritedCapabilities(", source);
        Assert.Contains("AgentIsolation.Route(", source);
        Assert.Contains("AgentIsolation.StripOrchestratorEnvironment(", source);
    }

    [Fact]
    public async Task File_read_rejects_a_sibling_whose_name_extends_the_root()
    {
        var (root, evil) = RootWithEvilSibling();
        await File.WriteAllTextAsync(Path.Combine(evil, "f"), "sibling secret");

        var result = await Service().ExecuteToolAsync("file.read", "../x-evil/f", root);

        Assert.False(result.Success);
        Assert.Equal("path traversal", result.Error);
        Assert.DoesNotContain("sibling secret", result.Output);
    }

    [Fact]
    public async Task File_write_rejects_a_sibling_whose_name_extends_the_root()
    {
        var (root, evil) = RootWithEvilSibling();
        var args = JsonSerializer.Serialize(new { path = "../x-evil/f", content = "planted" });

        var result = await Service().ExecuteToolAsync("file.write", args, root);

        Assert.False(result.Success);
        Assert.Equal("path traversal", result.Error);
        Assert.False(File.Exists(Path.Combine(evil, "f")));
    }

    [Fact]
    public async Task File_tools_still_reject_a_parent_escape()
    {
        var (root, _) = RootWithEvilSibling();
        await File.WriteAllTextAsync(Path.Combine(_tmp, "outside"), "outside secret");

        var read = await Service().ExecuteToolAsync("file.read", "../outside", root);
        var write = await Service().ExecuteToolAsync("file.write",
            JsonSerializer.Serialize(new { path = "../written", content = "planted" }), root);

        Assert.False(read.Success);
        Assert.Equal("path traversal", read.Error);
        Assert.False(write.Success);
        Assert.Equal("path traversal", write.Error);
        Assert.False(File.Exists(Path.Combine(_tmp, "written")));
    }

    [Fact]
    public async Task File_write_then_read_inside_the_root_works_including_nested_directories()
    {
        var (root, _) = RootWithEvilSibling();
        var svc = Service();

        var write = await svc.ExecuteToolAsync("file.write",
            JsonSerializer.Serialize(new { path = "a/b/c.txt", content = "nested" }), root);
        var read = await svc.ExecuteToolAsync("file.read", "a/b/c.txt", root);
        var topWrite = await svc.ExecuteToolAsync("file.write",
            JsonSerializer.Serialize(new { path = "top.txt", content = "top" }), root);
        var topRead = await svc.ExecuteToolAsync("file.read", "top.txt", root);

        Assert.True(write.Success, write.Error);
        Assert.Equal("nested", await File.ReadAllTextAsync(Path.Combine(root, "a", "b", "c.txt")));
        Assert.True(read.Success, read.Error);
        Assert.Equal("nested", read.Output);
        Assert.True(topWrite.Success, topWrite.Error);
        Assert.True(topRead.Success, topRead.Error);
        Assert.Equal("top", topRead.Output);
    }

    [Fact]
    public void File_tools_accept_paths_under_the_filesystem_root()
    {
        Assert.Equal("/etc/hostname", AIProviderService.SafePath("/", "etc/hostname"));
        Assert.Equal("/", AIProviderService.SafePath("/", "."));
    }

    [Fact]
    public async Task File_tools_accept_a_root_given_with_a_trailing_separator()
    {
        var (root, _) = RootWithEvilSibling();
        var svc = Service();

        var write = await svc.ExecuteToolAsync("file.write",
            JsonSerializer.Serialize(new { path = "sub/f.txt", content = "ok" }), root + Path.DirectorySeparatorChar);
        var read = await svc.ExecuteToolAsync("file.read", "sub/f.txt", root + Path.DirectorySeparatorChar);

        Assert.True(write.Success, write.Error);
        Assert.True(read.Success, read.Error);
        Assert.Equal("ok", read.Output);
    }

    private (string Root, string EvilSibling) RootWithEvilSibling()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tmp, "x")).FullName;
        var evil = Directory.CreateDirectory(Path.Combine(_tmp, "x-evil")).FullName;
        return (root, evil);
    }

    private static string StripComments(string source) =>
        Regex.Replace(source, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ILD.sln"))) return dir.FullName;
        throw new InvalidOperationException("ILD.sln not found above " + AppContext.BaseDirectory);
    }
}
