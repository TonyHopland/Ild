using System.Diagnostics;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>
/// What the mirror and the worktree pointer check do when something is wrong: a bundle that
/// is not what it claims, a mirror that is lost or half built, a pointer the agent replaced
/// with something that blocks. Every test runs once per mode under <see cref="AuditingRunner"/>.
/// </summary>
[Collection("Git")]
public class RepositoryManagerMirrorRobustnessTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ild-mirror-robust-" + Guid.NewGuid().ToString("N"));
    private readonly string _mirrors;
    private readonly string _worktrees;
    private readonly List<AuditingRunner> _runners = new();

    public RepositoryManagerMirrorRobustnessTests()
    {
        Directory.CreateDirectory(_tmp);
        _mirrors = Path.Combine(_tmp, "mirrors");
        _worktrees = Path.Combine(_tmp, "wt");
    }

    public void Dispose()
    {
        var violations = _runners.SelectMany(r => r.Violations).ToList();
        try { Directory.Delete(_tmp, recursive: true); } catch { }
        GC.SuppressFinalize(this);
        Assert.True(violations.Count == 0, "git isolation violated:\n" + string.Join("\n", violations));
    }

    private RepositoryManager NewManager(bool isolated, IProcessRunner? inner = null)
    {
        var runner = new AuditingRunner(isolated, inner: inner);
        runner.Forbid(_worktrees);
        runner.Forbid(Path.Combine(_tmp, "base"));
        _runners.Add(runner);
        return new RepositoryManager(runner, null, _worktrees, runner.ManagerAgent, _mirrors);
    }

    private async Task<(string Remote, string Base, GitAuthOptions Auth, string Worktree)> ClonedWithWorktreeAsync(RepositoryManager mgr, string branch)
    {
        var remote = GitCli.MakeRemote(_tmp, "remote");
        var basePath = Path.Combine(_tmp, "base");
        var auth = new GitAuthOptions(remote, "tok-" + Guid.NewGuid().ToString("N"), "Forgejo");
        var (ok, error) = await mgr.CloneAsync(remote, basePath, TestContext.Current.CancellationToken, auth);
        Assert.True(ok, error);
        GitCli.Run(basePath, "config", "user.name", "Tester");
        GitCli.Run(basePath, "config", "user.email", "t@t.io");
        var wt = await mgr.CreateWorktreeAsync(basePath, branch);
        File.WriteAllText(Path.Combine(wt, "run.txt"), "run output\n");
        Assert.True(await mgr.CommitAsync(wt, "run work"));
        return (remote, basePath, auth, wt);
    }

    /// <summary>Hands the orchestrator a bundle the test wrote, in place of the one the agent's git would.</summary>
    private sealed class ForgedBundleRunner(byte[] bundle) : IProcessRunner
    {
        private readonly IProcessRunner _inner = new ProcessRunner();

        public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, CancellationToken ct = default, IReadOnlyDictionary<string, string?>? environmentVariables = null, ProcessRunOptions? options = null)
        {
            if (options?.StandardOutputTo is { } sink && args.Contains("bundle"))
            {
                await sink.WriteAsync(bundle, ct);
                return new ProcessResult(0, string.Empty, string.Empty);
            }

            return await _inner.RunAsync(fileName, args, workingDirectory, ct, environmentVariables, options);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_bundle_holding_a_malformed_object_is_refused_and_nothing_reaches_the_remote(bool isolated)
    {
        const string branch = "ild/wi-30-run-1";
        var source = Path.Combine(_tmp, "forger");
        Directory.CreateDirectory(source);
        GitCli.Run(source, "init", "-b", "main");
        File.WriteAllText(Path.Combine(source, "f"), "x\n");
        GitCli.Run(source, "add", "-A");
        GitCli.Run(source, "commit", "-m", "base");
        var tree = GitCli.Out(source, "rev-parse", "HEAD^{tree}");
        var malformed = $"tree {tree}\nauthor nobody\ncommitter nobody\n\nbroken\n";
        var psi = new ProcessStartInfo("git") { WorkingDirectory = source, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "hash-object", "-t", "commit", "--literally", "-w", "--stdin" }) psi.ArgumentList.Add(a);
        using (var hash = Process.Start(psi)!)
        {
            await hash.StandardInput.WriteAsync(malformed);
            hash.StandardInput.Close();
            var sha = (await hash.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken)).Trim();
            await hash.WaitForExitAsync(TestContext.Current.CancellationToken);
            GitCli.Run(source, "update-ref", $"refs/heads/{branch}", sha);
        }
        var bundlePath = Path.Combine(_tmp, "forged.bundle");
        GitCli.Run(source, "bundle", "create", bundlePath, $"refs/heads/{branch}");

        var mgr = NewManager(isolated, new ForgedBundleRunner(File.ReadAllBytes(bundlePath)));
        var (remote, _, auth, wt) = await ClonedWithWorktreeAsync(mgr, branch);
        var before = AuditingRunner.BundleFilesUnderPrivateRoot();

        var (ok, error) = await mgr.PushAsync(wt, branch, TestContext.Current.CancellationToken, auth);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal("", GitCli.Out(remote, "for-each-ref", $"refs/heads/{branch}"));
        Assert.Empty(AuditingRunner.BundleFilesUnderPrivateRoot().Except(before));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_push_after_the_mirror_was_lost_keeps_the_other_remote_tracking_branches(bool isolated)
    {
        var mgr = NewManager(isolated);
        var (remote, basePath, auth, wt) = await ClonedWithWorktreeAsync(mgr, "ild/wi-31-run-1");
        Directory.Delete(Assert.Single(Directory.GetDirectories(_mirrors)), recursive: true);

        var (ok, error) = await mgr.PushAsync(wt, "ild/wi-31-run-1", TestContext.Current.CancellationToken, auth);

        Assert.True(ok, error);
        Assert.Equal(GitCli.Out(remote, "rev-parse", "refs/heads/main"), GitCli.Out(basePath, "rev-parse", "refs/remotes/origin/main"));
        Assert.Equal(GitCli.Out(wt, "rev-parse", "HEAD"), GitCli.Out(basePath, "rev-parse", "refs/remotes/origin/ild/wi-31-run-1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_half_built_mirror_is_rebuilt_on_the_next_use(bool isolated)
    {
        var mgr = NewManager(isolated);
        var (_, _, auth, wt) = await ClonedWithWorktreeAsync(mgr, "ild/wi-32-run-1");
        var mirror = Assert.Single(Directory.GetDirectories(_mirrors));
        File.Delete(Path.Combine(mirror, "HEAD"));
        Directory.CreateDirectory(mirror + ".building-left-by-a-crash");

        Assert.True(await mgr.FetchAsync(wt, TestContext.Current.CancellationToken, auth));

        Assert.Equal(mirror, Assert.Single(Directory.GetDirectories(_mirrors)));
        Assert.True(File.Exists(Path.Combine(mirror, "HEAD")));
    }

    [Theory]
    [InlineData(false, "dot-git")]
    [InlineData(true, "dot-git")]
    [InlineData(false, "back-pointer")]
    [InlineData(true, "back-pointer")]
    public async Task A_pipe_where_the_git_pointer_belongs_is_refused_instead_of_blocking(bool isolated, string where)
    {
        var mgr = NewManager(isolated);
        var (_, _, _, wt) = await ClonedWithWorktreeAsync(mgr, "ild/wi-33-run-1");
        var pointer = Path.Combine(wt, ".git");
        var target = where == "dot-git"
            ? pointer
            : Path.Combine(File.ReadAllText(pointer).Trim()["gitdir:".Length..].Trim(), "gitdir");
        File.Delete(target);
        using (var mkfifo = Process.Start(new ProcessStartInfo("mkfifo", target) { UseShellExecute = false })!)
        {
            await mkfifo.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, mkfifo.ExitCode);
        }

        // Off the test's thread: a pointer read that blocks must fail the test, not hang it.
        Assert.False(await Task.Run(() => mgr.CommitAsync(wt, "refused")).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.False(await Task.Run(() => mgr.ValidateWorktreeHealthAsync(wt)).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    private string PlantPrePush(string hooksDirectory, int exitCode)
    {
        var log = Path.Combine(_tmp, "pre-push.log");
        Directory.CreateDirectory(hooksDirectory);
        var hook = Path.Combine(hooksDirectory, "pre-push");
        var rejection = exitCode == 0 ? string.Empty : "echo \"rejected by the hook\" >&2\n";
        File.WriteAllText(hook, $"#!/bin/sh\n{{ echo \"args=$*\"; cat; }} >> '{log}'\n{rejection}exit {exitCode}\n");
        File.SetUnixFileMode(hook, (UnixFileMode)0b111_101_101);
        return log;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_passing_pre_push_hook_runs_before_the_push_with_the_line_git_would_give_it(bool isolated)
    {
        const string branch = "ild/wi-35-run-1";
        var mgr = NewManager(isolated);
        var (remote, basePath, auth, wt) = await ClonedWithWorktreeAsync(mgr, branch);
        var log = PlantPrePush(Path.Combine(basePath, ".git", "hooks"), 0);
        var first = GitCli.Out(wt, "rev-parse", "HEAD");

        var (ok, error) = await mgr.PushAsync(wt, branch, TestContext.Current.CancellationToken, auth);

        Assert.True(ok, error);
        Assert.Equal(first, GitCli.Out(remote, "rev-parse", $"refs/heads/{branch}"));
        Assert.Equal($"args=origin {remote}\nrefs/heads/{branch} {first} refs/heads/{branch} {new string('0', first.Length)}\n", File.ReadAllText(log));

        File.WriteAllText(Path.Combine(wt, "run.txt"), "second\n");
        Assert.True(await mgr.CommitAsync(wt, "second"));
        var second = GitCli.Out(wt, "rev-parse", "HEAD");
        File.Delete(log);
        Assert.True((await mgr.PushAsync(wt, branch, TestContext.Current.CancellationToken, auth)).Success);
        Assert.Equal($"args=origin {remote}\nrefs/heads/{branch} {second} refs/heads/{branch} {first}\n", File.ReadAllText(log));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failing_pre_push_hook_stops_the_push_and_says_why(bool isolated)
    {
        const string branch = "ild/wi-36-run-1";
        var mgr = NewManager(isolated);
        var (remote, basePath, auth, wt) = await ClonedWithWorktreeAsync(mgr, branch);
        PlantPrePush(Path.Combine(basePath, ".git", "hooks"), 1);
        var before = AuditingRunner.BundleFilesUnderPrivateRoot();

        var (ok, error) = await mgr.PushAsync(wt, branch, TestContext.Current.CancellationToken, auth);

        Assert.False(ok);
        Assert.Contains("pre-push hook failed", error);
        Assert.Contains("rejected by the hook", error);
        Assert.Equal("", GitCli.Out(remote, "for-each-ref", $"refs/heads/{branch}"));
        Assert.Empty(AuditingRunner.BundleFilesUnderPrivateRoot().Except(before));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pre_push_hook_found_through_core_hooksPath_runs(bool isolated)
    {
        const string branch = "ild/wi-37-run-1";
        var mgr = NewManager(isolated);
        var (remote, basePath, auth, wt) = await ClonedWithWorktreeAsync(mgr, branch);
        GitCli.Run(basePath, "config", "core.hooksPath", ".husky");
        var log = PlantPrePush(Path.Combine(wt, ".husky"), 1);

        var (ok, error) = await mgr.PushAsync(wt, branch, TestContext.Current.CancellationToken, auth);

        Assert.False(ok);
        Assert.Contains("rejected by the hook", error);
        Assert.StartsWith($"args=origin {remote}\n", File.ReadAllText(log));
        Assert.Equal("", GitCli.Out(remote, "for-each-ref", $"refs/heads/{branch}"));
    }

    [Fact]
    public async Task The_setgid_bit_the_store_root_hands_down_survives_the_mode_tightening()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("setgid inheritance is a Linux filesystem behaviour");
        Directory.CreateDirectory(_mirrors);
        File.SetUnixFileMode(_mirrors, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.SetGroup);
        var probe = Directory.CreateDirectory(Path.Combine(_mirrors, "probe")).FullName;
        if (!File.GetUnixFileMode(probe).HasFlag(UnixFileMode.SetGroup))
            Assert.Skip("this filesystem does not hand setgid down to new directories");
        Directory.Delete(probe);

        var mgr = NewManager(isolated: false);
        var (_, _, auth, wt) = await ClonedWithWorktreeAsync(mgr, "ild/wi-34-run-1");
        Assert.True(await mgr.FetchAsync(wt, TestContext.Current.CancellationToken, auth));

        var mirror = Assert.Single(Directory.GetDirectories(_mirrors));
        Assert.True(File.GetUnixFileMode(mirror).HasFlag(UnixFileMode.SetGroup));
    }
}
