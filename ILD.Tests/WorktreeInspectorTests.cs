using System.Diagnostics;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Local;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests;

[Collection(GitBundleScratchCollection.Name)]
public sealed class WorktreeInspectorTests : IDisposable
{
    private const string ItemId = "wi-1";

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ild-inspector-" + Guid.NewGuid().ToString("N"));
    private readonly RepositoryManager _repos;
    private readonly Mock<IWorkItemManager> _workItems = new();
    private readonly Guid _repositoryId = Guid.NewGuid();
    private string _worktree = "";

    public WorktreeInspectorTests()
    {
        Directory.CreateDirectory(_tmp);
        _repos = new RepositoryManager(new LocalRunHost(new ProcessRunner()), worktreesRoot: Path.Combine(_tmp, "worktrees"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private async Task<WorktreeInspector> InspectorAsync(Func<string, string>? seed = null)
    {
        var origin = Path.Combine(_tmp, "origin");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-b", "main");
        Git(origin, "config", "user.email", "t@t.io");
        Git(origin, "config", "user.name", "Tester");
        File.WriteAllText(Path.Combine(origin, "keep.txt"), "keep\n");
        File.WriteAllText(Path.Combine(origin, "mod.txt"), "original\n");
        Git(origin, "add", "-A");
        Git(origin, "commit", "-m", "seed");

        var work = Path.Combine(_tmp, "work");
        Git(_tmp, "clone", origin, work);
        Git(work, "config", "user.email", "t@t.io");
        Git(work, "config", "user.name", "Tester");

        _worktree = await _repos.CreateWorktreeAsync(work, "ild/wi-1-run-1");
        File.WriteAllText(Path.Combine(_worktree, "committed.txt"), "committed line\n");
        Git(_worktree, "add", "-A");
        Git(_worktree, "commit", "-m", "run commit");
        File.WriteAllText(Path.Combine(_worktree, "mod.txt"), "edited but not committed\n");
        File.WriteAllText(Path.Combine(_worktree, "untracked.txt"), "scratch needle\n");

        _workItems.Setup(m => m.GetWorkItemAsync(ItemId)).ReturnsAsync(new WorkItemView
        {
            Id = ItemId,
            WorktreePath = _worktree,
            RunRepositoryId = _repositoryId,
        });
        var providers = new Mock<IProviderStore>();
        providers.Setup(p => p.GetRepositoryByIdAsync(_repositoryId)).ReturnsAsync(new Repository { Id = _repositoryId, DefaultBranch = "main" });
        return new WorktreeInspector(_workItems.Object, _repos, new LocalRunHost(new ProcessRunner()), Mock.Of<ILoopRunStore>(), providers.Object);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Status_lists_uncommitted_and_untracked_files()
    {
        var inspector = await InspectorAsync();

        var status = await inspector.GetStatusAsync(ItemId, Ct);

        Assert.True(status.Ok, status.Message);
        Assert.Contains(" M mod.txt", status.Text);
        Assert.Contains("?? untracked.txt", status.Text);
        Assert.DoesNotContain("committed.txt", status.Text);
    }

    [Fact]
    public async Task Diff_covers_committed_and_uncommitted_changes_against_the_fork_point()
    {
        var inspector = await InspectorAsync();

        var diff = await inspector.GetDiffAsync(ItemId, null, false, Ct);

        Assert.True(diff.Ok, diff.Message);
        Assert.Contains("+committed line", diff.Text);
        Assert.Contains("+edited but not committed", diff.Text);
        Assert.DoesNotContain("keep.txt", diff.Text);
    }

    [Fact]
    public async Task Diff_for_one_untracked_path_shows_it_as_added_and_stat_only_summarises()
    {
        var inspector = await InspectorAsync();

        var untracked = await inspector.GetDiffAsync(ItemId, "untracked.txt", false, Ct);
        var stat = await inspector.GetDiffAsync(ItemId, null, true, Ct);

        Assert.True(untracked.Ok, untracked.Message);
        Assert.Contains("+scratch needle", untracked.Text);
        Assert.True(stat.Ok, stat.Message);
        Assert.Contains("mod.txt", stat.Text);
        Assert.DoesNotContain("+edited", stat.Text);
    }

    [Fact]
    public async Task ReadFile_returns_the_working_copy_including_uncommitted_edits()
    {
        var inspector = await InspectorAsync();

        var read = await inspector.ReadFileAsync(ItemId, "mod.txt", Ct);

        Assert.True(read.Ok, read.Message);
        Assert.Equal("edited but not committed\n", read.Text);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData(".git/config")]
    [InlineData("sub/.GIT/config")]
    [InlineData(".git")]
    [InlineData("a\0b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public async Task Paths_that_leave_the_worktree_or_enter_git_are_refused_by_every_path_taking_operation(string path)
    {
        var inspector = await InspectorAsync();

        Assert.Equal(WorktreeReadOutcome.Invalid, (await inspector.ReadFileAsync(ItemId, path, Ct)).Outcome);
        if (path.Length > 0)
        {
            Assert.Equal(WorktreeReadOutcome.Invalid, (await inspector.GetDiffAsync(ItemId, path, false, Ct)).Outcome);
            Assert.Equal(WorktreeReadOutcome.Invalid, (await inspector.SearchAsync(ItemId, "x", false, path, Ct)).Outcome);
        }
    }

    [Fact]
    public async Task A_symlink_leading_out_of_the_worktree_is_not_followed()
    {
        var inspector = await InspectorAsync();
        var secret = Path.Combine(_tmp, "secret.txt");
        File.WriteAllText(secret, "top secret\n");
        File.CreateSymbolicLink(Path.Combine(_worktree, "escape.txt"), secret);
        Directory.CreateSymbolicLink(Path.Combine(_worktree, "escape-dir"), _tmp);

        var file = await inspector.ReadFileAsync(ItemId, "escape.txt", Ct);
        var viaDirectory = await inspector.ReadFileAsync(ItemId, "escape-dir/secret.txt", Ct);

        Assert.Equal(WorktreeReadOutcome.NotFound, file.Outcome);
        Assert.Equal(WorktreeReadOutcome.NotFound, viaDirectory.Outcome);
        Assert.DoesNotContain("top secret", file.Text + viaDirectory.Text);
    }

    [Fact]
    public async Task A_symlink_into_dot_git_is_not_read()
    {
        var inspector = await InspectorAsync();
        var gitDir = GitOut(_worktree, "rev-parse", "--git-dir");
        File.CreateSymbolicLink(Path.Combine(_worktree, "cfg"), Path.Combine(gitDir, "HEAD"));
        File.CreateSymbolicLink(Path.Combine(_worktree, "inner"), Path.Combine(_worktree, ".git"));

        Assert.Equal(WorktreeReadOutcome.NotFound, (await inspector.ReadFileAsync(ItemId, "cfg", Ct)).Outcome);
        Assert.Equal(WorktreeReadOutcome.NotFound, (await inspector.ReadFileAsync(ItemId, "inner", Ct)).Outcome);
    }

    [Fact]
    public async Task A_missing_file_and_a_directory_are_not_found()
    {
        var inspector = await InspectorAsync();
        Directory.CreateDirectory(Path.Combine(_worktree, "dir"));

        Assert.Equal(WorktreeReadOutcome.NotFound, (await inspector.ReadFileAsync(ItemId, "nope.txt", Ct)).Outcome);
        Assert.Equal(WorktreeReadOutcome.NotFound, (await inspector.ReadFileAsync(ItemId, "dir", Ct)).Outcome);
    }

    [Fact]
    public async Task A_binary_file_is_reported_by_size_not_shown()
    {
        var inspector = await InspectorAsync();
        File.WriteAllBytes(Path.Combine(_worktree, "blob.bin"), [1, 2, 0, 3, 4]);

        var read = await inspector.ReadFileAsync(ItemId, "blob.bin", Ct);

        Assert.True(read.Ok, read.Message);
        Assert.Equal("[binary file, 5 bytes: content not shown]", read.Text);
    }

    [Fact]
    public async Task A_large_file_is_cut_at_the_byte_cap_with_a_marker()
    {
        var inspector = await InspectorAsync();
        var total = WorktreeInspector.MaxOutputBytes * 2;
        File.WriteAllText(Path.Combine(_worktree, "big.txt"), new string('x', total));

        var read = await inspector.ReadFileAsync(ItemId, "big.txt", Ct);

        Assert.True(read.Ok, read.Message);
        Assert.True(read.Truncated);
        Assert.Equal(WorktreeInspector.MaxOutputBytes, read.OmittedBytes);
        Assert.EndsWith($"[truncated: {WorktreeInspector.MaxOutputBytes} more bytes not shown]", read.Text);
    }

    [Fact]
    public async Task A_file_with_many_lines_is_cut_at_the_line_cap_with_a_marker()
    {
        var inspector = await InspectorAsync();
        File.WriteAllText(Path.Combine(_worktree, "lines.txt"), string.Concat(Enumerable.Repeat("a\n", WorktreeInspector.MaxOutputLines + 50)));

        var read = await inspector.ReadFileAsync(ItemId, "lines.txt", Ct);

        Assert.True(read.Truncated);
        Assert.Equal(100, read.OmittedBytes);
        Assert.Equal(WorktreeInspector.MaxOutputLines, read.Text.Split('\n').Count(line => line == "a"));
    }

    [Fact]
    public async Task A_huge_diff_is_cut_at_the_byte_cap_with_a_marker()
    {
        var inspector = await InspectorAsync();
        File.WriteAllText(Path.Combine(_worktree, "mod.txt"), string.Concat(Enumerable.Repeat(new string('y', 99) + "\n", 5000)));

        var diff = await inspector.GetDiffAsync(ItemId, "mod.txt", false, Ct);

        Assert.True(diff.Ok, diff.Message);
        Assert.True(diff.Truncated);
        Assert.Contains("[truncated:", diff.Text);
        Assert.True(diff.Text.Length < WorktreeInspector.MaxOutputBytes + 100);
    }

    [Fact]
    public async Task Search_finds_tracked_and_untracked_text_and_reports_no_matches()
    {
        var inspector = await InspectorAsync();

        var hit = await inspector.SearchAsync(ItemId, "needle", false, null, Ct);
        var scoped = await inspector.SearchAsync(ItemId, "scratch", false, "mod.txt", Ct);
        var miss = await inspector.SearchAsync(ItemId, "absent-string", false, null, Ct);

        Assert.Contains("untracked.txt:1:scratch needle", hit.Text);
        Assert.Equal("No matches.", scoped.Text);
        Assert.True(miss.Ok, miss.Message);
        Assert.Equal("No matches.", miss.Text);
    }

    [Fact]
    public async Task Search_treats_the_pattern_as_a_fixed_string_unless_regex_and_never_as_an_option()
    {
        var inspector = await InspectorAsync();
        File.WriteAllText(Path.Combine(_worktree, "flags.txt"), "--output=pwned and a.c\n");

        var option = await inspector.SearchAsync(ItemId, "--output=pwned", false, null, Ct);
        var literal = await inspector.SearchAsync(ItemId, "a.c", false, null, Ct);
        var regex = await inspector.SearchAsync(ItemId, "a.c", true, null, Ct);

        Assert.Contains("flags.txt:1:", option.Text);
        Assert.Contains("flags.txt:1:", literal.Text);
        Assert.Contains("flags.txt:1:", regex.Text);
        Assert.False(File.Exists(Path.Combine(_worktree, "pwned")));
    }

    [Fact]
    public async Task Log_lists_only_the_commits_the_run_added()
    {
        var inspector = await InspectorAsync();

        var log = await inspector.GetLogAsync(ItemId, 10, Ct);

        Assert.True(log.Ok, log.Message);
        Assert.Contains("run commit", log.Text);
        Assert.DoesNotContain("seed", log.Text);
        Assert.Equal(WorktreeReadOutcome.Invalid, (await inspector.GetLogAsync(ItemId, 0, Ct)).Outcome);
        Assert.Equal(WorktreeReadOutcome.Invalid, (await inspector.GetLogAsync(ItemId, WorktreeInspector.MaxLogEntries + 1, Ct)).Outcome);
    }

    [Fact]
    public async Task Commit_shows_a_commit_of_the_branch_and_refuses_anything_else()
    {
        var inspector = await InspectorAsync();
        var head = GitOut(_worktree, "rev-parse", "HEAD");
        var stray = GitOut(_worktree, "commit-tree", "HEAD^{tree}", "-p", "HEAD", "-m", "not on the branch");

        var shown = await inspector.GetCommitAsync(ItemId, head, Ct);
        var abbreviated = await inspector.GetCommitAsync(ItemId, head[..8], Ct);

        Assert.True(shown.Ok, shown.Message);
        Assert.Contains("run commit", shown.Text);
        Assert.Contains("+committed line", shown.Text);
        Assert.True(abbreviated.Ok, abbreviated.Message);
        Assert.Equal(WorktreeReadOutcome.NotFound, (await inspector.GetCommitAsync(ItemId, stray, Ct)).Outcome);
        Assert.Equal(WorktreeReadOutcome.NotFound, (await inspector.GetCommitAsync(ItemId, new string('a', 40), Ct)).Outcome);
        foreach (var bad in new[] { "HEAD", "--output=x", "abc", "ABCDEF1", head + "; rm -rf /", "main~1" })
            Assert.Equal(WorktreeReadOutcome.Invalid, (await inspector.GetCommitAsync(ItemId, bad, Ct)).Outcome);
    }

    [Fact]
    public async Task A_work_item_without_a_worktree_gets_a_clear_error()
    {
        var inspector = await InspectorAsync();
        _workItems.Setup(m => m.GetWorkItemAsync("none")).ReturnsAsync(new WorkItemView { Id = "none" });
        _workItems.Setup(m => m.GetWorkItemAsync("gone")).ReturnsAsync(new WorkItemView { Id = "gone", WorktreePath = Path.Combine(_tmp, "removed") });

        var noPath = await inspector.GetStatusAsync("none", Ct);
        var removed = await inspector.ReadFileAsync("gone", "a.txt", Ct);
        var unknown = await inspector.GetLogAsync("unknown", 5, Ct);

        Assert.Equal(WorktreeReadOutcome.NoWorktree, noPath.Outcome);
        Assert.Contains("worktree", noPath.Message);
        Assert.Equal(WorktreeReadOutcome.NoWorktree, removed.Outcome);
        Assert.Equal(WorktreeReadOutcome.WorkItemNotFound, unknown.Outcome);
    }

    [Fact]
    public async Task Reads_resolve_only_the_work_item_they_were_asked_about()
    {
        var inspector = await InspectorAsync();

        await inspector.GetStatusAsync(ItemId, Ct);
        await inspector.ReadFileAsync(ItemId, "mod.txt", Ct);
        await inspector.SearchAsync(ItemId, "needle", false, null, Ct);

        _workItems.Verify(m => m.GetWorkItemAsync(It.Is<string>(id => id != ItemId)), Times.Never);
    }

    [Fact]
    public async Task Only_a_fixed_set_of_read_only_git_commands_is_ever_run_and_input_never_becomes_an_option()
    {
        var repos = new Mock<IRepositoryManager>();
        var calls = new List<string[]>();
        repos.Setup(r => r.ValidateWorktreeHealthAsync(It.IsAny<string>())).ReturnsAsync(true);
        repos.Setup(r => r.ListWorktreeCommitsAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new ILD.Data.DTOs.WorktreeCommitsResponse { BaseSha = new string('b', 40) });
        repos.Setup(r => r.ReadGitAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, int, CancellationToken>((_, args, _, _) => calls.Add([.. args]))
            .ReturnsAsync(new CappedGitOutput(0, new string('c', 40), 0, ""));
        var host = new Mock<IRunHost>();
        host.Setup(h => h.DirectoryExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(ItemId)).ReturnsAsync(new WorkItemView { Id = ItemId, WorktreePath = "/wt" });
        var inspector = new WorktreeInspector(workItems.Object, repos.Object, host.Object, Mock.Of<ILoopRunStore>(), Mock.Of<IProviderStore>());
        const string hostile = "x; rm -rf / $(id) `id` --output=/tmp/pwn";

        await inspector.GetStatusAsync(ItemId, Ct);
        await inspector.GetDiffAsync(ItemId, hostile, false, Ct);
        await inspector.SearchAsync(ItemId, hostile, true, hostile, Ct);
        await inspector.GetLogAsync(ItemId, 5, Ct);
        await inspector.GetCommitAsync(ItemId, new string('a', 40), Ct);

        var verbs = calls.Select(c => c[0]).ToHashSet();
        Assert.Subset(new HashSet<string> { "status", "diff", "grep", "log", "show", "ls-files", "rev-parse", "merge-base" }, verbs);
        Assert.DoesNotContain(calls, c => c.Any(a => a.StartsWith("--output", StringComparison.Ordinal)));
        foreach (var call in calls)
        {
            var separator = Array.IndexOf(call, "--");
            for (var i = 0; i < call.Length; i++)
            {
                if (!call[i].Contains("rm -rf")) continue;
                Assert.True(
                    call[i] == hostile && (separator >= 0 && i > separator || i > 0 && call[i - 1] == "-e"),
                    $"user text reached git outside a pattern or path slot: {string.Join(' ', call)}");
            }
        }
    }

    private static string GitOut(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
        return stdout.Trim();
    }

    private static void Git(string cwd, params string[] args) => GitOut(cwd, args);
}
