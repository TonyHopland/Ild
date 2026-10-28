using System.Diagnostics;
using System.Runtime.Versioning;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>
/// The mirror hands itself to the shared group: the agent reads it through that group, so it
/// cannot depend on the store having been set up that way by the deployment.
/// </summary>
[SupportedOSPlatform("linux")]
public class GitMirrorGroupTests : IDisposable
{
    private const string Remote = "https://forge.example.test/team/app.git";

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ild-mirror-group-" + Guid.NewGuid().ToString("N"));

    public GitMirrorGroupTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static string Run(params string[] command)
    {
        var psi = new ProcessStartInfo(command[0]) { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var a in command.Skip(1)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return output;
    }

    private static string GroupOf(string path) => Run("stat", "-c", "%g", path);

    /// <summary>A group this user belongs to besides its primary one, which is what the orchestrator has with the agent's.</summary>
    private static string OtherGroup()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("groups are a Linux filesystem behaviour");
        var primary = Run("id", "-g");
        var other = Run("id", "-G").Split(' ').FirstOrDefault(g => g != primary);
        if (other is null) Assert.Skip("this user has no group besides its primary one");
        return other;
    }

    private GitMirror NewMirror(string root, string? group)
    {
        var runner = new ProcessRunner();
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["HOME"] = _tmp,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
        };
        return new GitMirror(
            root, group, sharedWithAgent: true,
            (directory, args, _, ct, _) => runner.RunAsync("git", args, directory, ct, environment, new ProcessRunOptions(CleanEnvironment: true)));
    }

    [Fact]
    public async Task A_store_root_the_mirror_makes_is_in_the_agent_group_and_hands_it_down()
    {
        var group = OtherGroup();
        var root = Path.Combine(_tmp, "mirrors");
        var mirror = NewMirror(root, group);

        var (session, error) = await mirror.OpenAsync(Remote, TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        using (session)
        {
            Assert.Null(error);
            Assert.Equal(group, GroupOf(root));
            Assert.True(File.GetUnixFileMode(root).HasFlag(UnixFileMode.SetGroup));
            Assert.Equal(group, GroupOf(session.Path));
            Assert.Equal(group, GroupOf(Path.Combine(session.Path, "HEAD")));
        }
    }

    [Fact]
    public async Task A_root_and_a_mirror_in_the_wrong_group_are_corrected_before_the_agent_can_read()
    {
        var group = OtherGroup();
        var root = Path.Combine(_tmp, "mirrors");
        var first = NewMirror(root, group);
        var (made, _) = await first.OpenAsync(Remote, TestContext.Current.CancellationToken);
        string path;
        using (made!) path = made!.Path;
        var primary = Run("id", "-g");
        Run("chgrp", "-R", primary, root);
        File.SetUnixFileMode(root, File.GetUnixFileMode(root) & ~UnixFileMode.SetGroup);
        Assert.Equal(primary, GroupOf(Path.Combine(path, "HEAD")));

        var (session, error) = await NewMirror(root, group).OpenAsync(Remote, TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        using (session)
        {
            Assert.Null(error);
            await session.ReadableByAgentAsync(p => Task.FromResult(p), TestContext.Current.CancellationToken);

            Assert.Equal(group, GroupOf(root));
            Assert.True(File.GetUnixFileMode(root).HasFlag(UnixFileMode.SetGroup));
            Assert.Equal(group, GroupOf(session.Path));
            Assert.Equal(group, GroupOf(Path.Combine(session.Path, "HEAD")));
            Assert.Equal(group, GroupOf(Path.Combine(session.Path, "objects")));
        }
    }

    [Fact]
    public async Task A_group_the_orchestrator_is_not_in_is_reported_instead_of_surfacing_later_as_a_missing_repository()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("groups are a Linux filesystem behaviour");
        var member = Run("id", "-G").Split(' ').Select(uint.Parse).ToHashSet();
        var foreign = Enumerable.Range(40000, 1000).Select(g => (uint)g).First(g => !member.Contains(g));
        if (Run("id", "-u") == "0") Assert.Skip("root may move files into any group");
        var mirror = NewMirror(Path.Combine(_tmp, "mirrors"), foreign.ToString());

        var (session, error) = await mirror.OpenAsync(Remote, TestContext.Current.CancellationToken);

        Assert.Null(session);
        Assert.Contains($"not in group {foreign}", error);
    }

    [Fact]
    public async Task The_mirror_moves_into_the_shared_group_and_never_the_agents_primary_group()
    {
        var shared = OtherGroup();
        var primary = Run("id", "-g");
        var root = Path.Combine(_tmp, "mirrors");
        Directory.CreateDirectory(root);
        Run("chgrp", primary, root);
        var mirror = NewMirror(root, shared);

        var (session, error) = await mirror.OpenAsync(Remote, TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        using (session)
        {
            Assert.Null(error);
            Assert.Equal(shared, GroupOf(root));
            Assert.Equal(shared, GroupOf(session.Path));
            Assert.NotEqual(primary, GroupOf(Path.Combine(session.Path, "HEAD")));
        }
    }

    [Fact]
    public async Task A_missing_shared_group_with_a_setgid_group_readable_root_works_without_changing_its_group()
    {
        var group = OtherGroup();
        var root = Path.Combine(_tmp, "mirrors");
        Directory.CreateDirectory(root);
        Run("chgrp", group, root);
        File.SetUnixFileMode(root, UnixFileMode.SetGroup | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        var (session, error) = await NewMirror(root, null).OpenAsync(Remote, TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        using (session)
        {
            Assert.Null(error);
            Assert.Equal(group, GroupOf(root));
            Assert.Equal(group, GroupOf(session.Path));
            Assert.Equal(group, GroupOf(Path.Combine(session.Path, "HEAD")));
        }
    }

    [Fact]
    public async Task A_missing_shared_group_with_a_root_that_is_not_setgid_names_SHARED_GROUP()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("groups are a Linux filesystem behaviour");
        var root = Path.Combine(_tmp, "mirrors");
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        var (session, error) = await NewMirror(root, null).OpenAsync(Remote, TestContext.Current.CancellationToken);

        Assert.Null(session);
        Assert.Contains("SHARED_GROUP", error);
        Assert.Contains("ILD_SHARED_GROUP", error);
    }

    [Fact]
    public void The_entrypoint_exports_the_group_it_gives_the_mirror_root()
    {
        var root = RepositoryRoot();
        var entrypoint = File.ReadAllText(Path.Combine(root, "entrypoint.sh"));
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));

        Assert.Contains("export ILD_SHARED_GROUP=\"$SHARED_GROUP\"", entrypoint);
        Assert.DoesNotContain("export ILD_SHARED_GROUP=\"$AGENT_GROUP\"", entrypoint);
        // The group ensure_shared_ro gives each SHARED_RO_DIRS entry is SHARED_GROUP, and the mirror root is one.
        Assert.Contains("chgrp -R \"$SHARED_GROUP\" \"$path\"", entrypoint[entrypoint.IndexOf("ensure_shared_ro() {", StringComparison.Ordinal)..]);
        Assert.Matches("ENV SHARED_RO_DIRS=\"[^\"]*/data/git-mirrors", dockerfile);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ILD.sln"))) return dir.FullName;
        throw new InvalidOperationException($"no ILD.sln above {AppContext.BaseDirectory}");
    }
}
