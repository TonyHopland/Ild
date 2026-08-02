using System.Diagnostics;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

[Collection("Git")]
public class RepositoryManagerTests : IDisposable
{
    private sealed class RecordingRunner : IProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Args, string? WorkingDirectory, IReadOnlyDictionary<string, string?>? Environment)> Calls { get; } = new();

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, CancellationToken ct = default, IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            Calls.Add((fileName, args, workingDirectory, environmentVariables == null ? null : new Dictionary<string, string?>(environmentVariables)));
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private readonly string _tmp;
    private readonly string _repo;

    public RepositoryManagerTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ild-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _repo = Path.Combine(_tmp, "repo");
        Directory.CreateDirectory(_repo);

        Git(_repo, "init", "-b", "main");
        Git(_repo, "config", "user.email", "t@t.io");
        Git(_repo, "config", "user.name", "Tester");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "init");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task CreateWorktree_creates_a_new_branch_and_directory()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-x");

        Assert.True(Directory.Exists(path));
        Assert.True(File.Exists(Path.Combine(path, "README.md")));

        Assert.True((await mgr.ValidateWorktreeHealthAsync(path)));
    }

    [Fact]
    public async Task CreateWorktree_supports_branch_names_with_slashes()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var path = await mgr.CreateWorktreeAsync(_repo, "ild/wi-11");

        Assert.EndsWith(Path.Combine("ild", "wi-11"), path);
        Assert.True(Directory.Exists(path));
        Assert.True(File.Exists(Path.Combine(path, "README.md")));
        Assert.True((await mgr.ValidateWorktreeHealthAsync(path)));
    }

    [Fact]
    public async Task Commit_and_diff_round_trip()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-y");
        File.WriteAllText(Path.Combine(path, "new.txt"), "content\n");

        Assert.True((await mgr.CommitAsync(path, "add file")));

        var diff = await mgr.GetDiffAsync(path);
        Assert.NotNull(diff);

        // CommitAsync runs `git add -A`, so anything the worktree setup dropped
        // on disk rides along into the run commit — and from there into the PR.
        // Only the agent's own edit belongs here.
        var committed = GitOutput(path, "show", "--pretty=", "--name-only", "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToList();
        Assert.Equal(new[] { "new.txt" }, committed);
    }

    [Fact]
    public async Task Commit_never_pushes_an_untracked_env_file_even_without_gitignore()
    {
        // The repo custom .env is injected as process env, but a preview/install step
        // (or the repo) could materialise it on disk. Even with no .gitignore entry,
        // an untracked .env must never ride into the run commit — and thence the PR.
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-secret");

        File.WriteAllText(Path.Combine(path, ".env"), "API_TOKEN=super-secret\n");
        File.WriteAllText(Path.Combine(path, ".ild.env"), "DB_PASSWORD=hunter2\n");
        File.WriteAllText(Path.Combine(path, "app.txt"), "code\n");

        Assert.True((await mgr.CommitAsync(path, "add app")));

        var committed = GitOutput(path, "show", "--pretty=", "--name-only", "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToList();
        // The agent's real edit is committed; the secret files are held back.
        Assert.Contains("app.txt", committed);
        Assert.DoesNotContain(".env", committed);
        Assert.DoesNotContain(".ild.env", committed);
    }

    [Fact]
    public async Task Commit_still_tracks_changes_to_a_deliberately_committed_env_file()
    {
        // The exclude only affects untracked paths, so a repo that intentionally
        // tracks its own .env keeps working — its edits still commit normally.
        Git(_repo, "config", "user.email", "t@t.io");
        File.WriteAllText(Path.Combine(_repo, ".env"), "PUBLIC=1\n");
        Git(_repo, "add", "-f", ".env");
        Git(_repo, "commit", "-m", "track env");

        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-tracked-env");

        File.WriteAllText(Path.Combine(path, ".env"), "PUBLIC=2\n");
        Assert.True((await mgr.CommitAsync(path, "edit tracked env")));

        var committed = GitOutput(path, "show", "--pretty=", "--name-only", "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToList();
        Assert.Contains(".env", committed);
    }

    [Fact]
    public async Task CreateWorktree_does_not_write_tool_config_into_the_worktree()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        // The seed repo has no opencode.json, so anything present afterwards was
        // synthesized by worktree creation rather than checked out from the repo.
        Assert.False(File.Exists(Path.Combine(_repo, "opencode.json")));

        var path = await mgr.CreateWorktreeAsync(_repo, "feature-no-stub");

        Assert.False(File.Exists(Path.Combine(path, "opencode.json")));
    }

    [Fact]
    public async Task DestroyWorktree_removes_directory()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-z");
        Assert.True(Directory.Exists(path));

        await mgr.DestroyWorktreeAsync(path);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task CreateWorktree_recreates_stale_non_repo_directory()
    {
        var root = Path.Combine(_tmp, "wt");
        var stalePath = Path.Combine(root, "ild", "wi-11");
        Directory.CreateDirectory(stalePath);
        File.WriteAllText(Path.Combine(stalePath, "leftover.txt"), "stale");

        var mgr = new RepositoryManager(worktreesRoot: root);
        var path = await mgr.CreateWorktreeAsync(_repo, "ild/wi-11");

        Assert.Equal(stalePath, path);
        Assert.True(File.Exists(Path.Combine(path, "README.md")));
        Assert.False(File.Exists(Path.Combine(path, "leftover.txt")));
        Assert.True((await mgr.ValidateWorktreeHealthAsync(path)));
    }

    [Fact]
    public async Task ReadFile_returns_content_and_blocks_path_traversal()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var path = await mgr.CreateWorktreeAsync(_repo, "feature-r");

        Assert.Contains("hello", (await mgr.ReadFileAsync(path, "README.md")));
        Assert.Null((await mgr.ReadFileAsync(path, "../README.md")));
    }

    [Fact]
    public async Task CloneAsync_passes_git_askpass_environment_when_api_key_is_present()
    {
        var runner = new RecordingRunner();
        var mgr = new RepositoryManager(runner, worktreesRoot: Path.Combine(_tmp, "wt"));
        var targetPath = Path.Combine(_tmp, "clone-target");

        await mgr.CloneAsync(
            "https://gitlab.example.com/group/repo.git",
            targetPath,
            auth: new GitAuthOptions("https://gitlab.example.com/group/repo.git", "token-123", "GitLab"));

        Assert.Single(runner.Calls);
        Assert.NotNull(runner.Calls[0].Environment);
        Assert.False(string.IsNullOrWhiteSpace(runner.Calls[0].Environment!["GIT_ASKPASS"]));
        Assert.Equal("oauth2", runner.Calls[0].Environment!["ILD_GIT_USERNAME"]);
        Assert.Equal("token-123", runner.Calls[0].Environment!["ILD_GIT_PASSWORD"]);
    }

    [Fact]
    public async Task PushAsync_uses_non_blank_username_for_forgejo_style_remotes()
    {
        var runner = new RecordingRunner();
        var mgr = new RepositoryManager(runner, worktreesRoot: Path.Combine(_tmp, "wt"));

        await mgr.PushAsync(
            _repo,
            "ild/wi-17",
            auth: new GitAuthOptions("https://git.kube/team/repo.git", "token-123", "Forgejo"));

        Assert.Single(runner.Calls);
        Assert.NotNull(runner.Calls[0].Environment);
        Assert.Equal("git", runner.Calls[0].Environment!["ILD_GIT_USERNAME"]);
        Assert.Equal("token-123", runner.Calls[0].Environment!["ILD_GIT_PASSWORD"]);
    }

    [Fact]
    public async Task DeleteLocalBranchAsync_deletes_existing_branch()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        // Create a branch to delete
        Git(_repo, "branch", "to-delete");

        var success = await mgr.DeleteLocalBranchAsync(_repo, "to-delete");
        Assert.True(success);

        // Verify branch is gone
        var listResult = GitOutput(_repo, "branch", "--list", "to-delete");
        Assert.Empty(listResult.Trim());
    }

    [Fact]
    public async Task DeleteLocalBranchAsync_calls_git_branch_D_with_correct_args()
    {
        var runner = new RecordingRunner();
        var mgr = new RepositoryManager(runner, worktreesRoot: Path.Combine(_tmp, "wt"));

        await mgr.DeleteLocalBranchAsync(_repo, "does-not-exist");

        Assert.Single(runner.Calls);
        Assert.Equal("git", runner.Calls[0].FileName);
        Assert.Contains("-D", runner.Calls[0].Args);
        Assert.Contains("does-not-exist", runner.Calls[0].Args);
    }

    [Fact]
    public async Task ResolveBaseRepoPathAsync_returns_base_repo_from_worktree()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var wtPath = await mgr.CreateWorktreeAsync(_repo, "resolve-test");

        var basePath = await mgr.ResolveBaseRepoPathAsync(wtPath);
        Assert.NotNull(basePath);
        Assert.Equal(_repo, Path.GetFullPath(basePath));
    }

    [Fact]
    public async Task ResetHardAsync_calls_git_reset_hard_with_revision()
    {
        var runner = new RecordingRunner();
        var mgr = new RepositoryManager(runner, worktreesRoot: Path.Combine(_tmp, "wt"));

        var success = await mgr.ResetHardAsync(_repo, "origin/main");
        Assert.True(success);

        Assert.Single(runner.Calls);
        Assert.Equal("git", runner.Calls[0].FileName);
        Assert.Contains("reset", runner.Calls[0].Args);
        Assert.Contains("--hard", runner.Calls[0].Args);
        Assert.Contains("origin/main", runner.Calls[0].Args);
    }

    [Fact]
    public async Task ResetHardAsync_resets_working_tree_to_revision()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));

        // Create a commit to have something to reset from
        File.WriteAllText(Path.Combine(_repo, "file-a.txt"), "content a\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add file-a");
        var commitA = GitOutput(_repo, "rev-parse", "HEAD").Trim();

        // Create another commit
        File.WriteAllText(Path.Combine(_repo, "file-b.txt"), "content b\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add file-b");

        // Reset back to first commit
        var success = await mgr.ResetHardAsync(_repo, commitA);
        Assert.True(success);

        // file-b.txt should be gone after reset --hard
        Assert.False(File.Exists(Path.Combine(_repo, "file-b.txt")));
        Assert.True(File.Exists(Path.Combine(_repo, "file-a.txt")));
    }

    [Fact]
    public async Task ListWorktreeFiles_tags_each_file_with_its_change_status()
    {
        var (work, mgr) = CloneWithOrigin();
        var wt = await mgr.CreateWorktreeAsync(work, "feature-files");

        // Committed changes on the branch: add, modify, delete.
        File.WriteAllText(Path.Combine(wt, "added.txt"), "new file\n");
        File.WriteAllText(Path.Combine(wt, "mod.txt"), "changed\n");
        File.Delete(Path.Combine(wt, "gone.txt"));
        Git(wt, "add", "-A");
        Git(wt, "commit", "-m", "branch work");
        // An uncommitted, untracked file must still register as added.
        File.WriteAllText(Path.Combine(wt, "untracked.txt"), "scratch\n");

        var files = await mgr.ListWorktreeFilesAsync(wt);
        var byPath = files.ToDictionary(f => f.Path, f => f.ChangeStatus);

        Assert.Equal("added", byPath["added.txt"]);
        Assert.Equal("added", byPath["untracked.txt"]);
        Assert.Equal("modified", byPath["mod.txt"]);
        Assert.Equal("deleted", byPath["gone.txt"]);
        Assert.Equal("none", byPath["keep.txt"]);
    }

    [Fact]
    public async Task ReadWorktreeFile_returns_content_and_diff_for_changed_files()
    {
        var (work, mgr) = CloneWithOrigin();
        var wt = await mgr.CreateWorktreeAsync(work, "feature-read");

        File.WriteAllText(Path.Combine(wt, "mod.txt"), "changed\n");
        File.Delete(Path.Combine(wt, "gone.txt"));
        Git(wt, "add", "-A");
        Git(wt, "commit", "-m", "edit");
        File.WriteAllText(Path.Combine(wt, "untracked.txt"), "scratch\n");

        var modified = await mgr.ReadWorktreeFileAsync(wt, "mod.txt");
        Assert.NotNull(modified);
        Assert.Equal("modified", modified!.ChangeStatus);
        Assert.Contains("changed", modified.Content);
        Assert.Contains("+changed", modified.Diff);

        var untracked = await mgr.ReadWorktreeFileAsync(wt, "untracked.txt");
        Assert.NotNull(untracked);
        Assert.Equal("added", untracked!.ChangeStatus);
        Assert.Contains("scratch", untracked.Content);
        Assert.Contains("+scratch", untracked.Diff);

        var deleted = await mgr.ReadWorktreeFileAsync(wt, "gone.txt");
        Assert.NotNull(deleted);
        Assert.Equal("deleted", deleted!.ChangeStatus);
        Assert.Null(deleted.Content);
        Assert.NotNull(deleted.Diff);

        var unchanged = await mgr.ReadWorktreeFileAsync(wt, "keep.txt");
        Assert.NotNull(unchanged);
        Assert.Equal("none", unchanged!.ChangeStatus);
        Assert.Null(unchanged.Diff);
        Assert.Contains("keep", unchanged.Content);
    }

    [Fact]
    public async Task ListWorktreeFiles_diffs_committed_work_against_stored_default_branch_when_origin_head_unset()
    {
        var (work, mgr) = CloneWithOrigin();
        // Production worktrees don't carry refs/remotes/origin/HEAD; reproduce
        // that so the diff can only anchor via the stored default branch.
        Git(work, "symbolic-ref", "-d", "refs/remotes/origin/HEAD");

        var wt = await mgr.CreateWorktreeAsync(work, "feature-stored-branch");
        File.WriteAllText(Path.Combine(wt, "added.txt"), "new file\n");
        File.WriteAllText(Path.Combine(wt, "mod.txt"), "changed\n");
        Git(wt, "add", "-A");
        Git(wt, "commit", "-m", "branch work"); // committed, unpushed, clean tree

        var files = await mgr.ListWorktreeFilesAsync(wt, "main");
        var byPath = files.ToDictionary(f => f.Path, f => f.ChangeStatus);

        Assert.Equal("added", byPath["added.txt"]);
        Assert.Equal("modified", byPath["mod.txt"]);

        // Without the stored branch, origin/HEAD is unresolvable and the diff
        // silently collapses — the very regression this story fixes.
        var collapsed = await mgr.ListWorktreeFilesAsync(wt);
        Assert.Equal("none", collapsed.Single(f => f.Path == "mod.txt").ChangeStatus);
    }

    [Fact]
    public async Task ReadWorktreeFile_diffs_against_stored_default_branch_when_origin_head_unset()
    {
        var (work, mgr) = CloneWithOrigin();
        Git(work, "symbolic-ref", "-d", "refs/remotes/origin/HEAD");

        var wt = await mgr.CreateWorktreeAsync(work, "feature-stored-read");
        File.WriteAllText(Path.Combine(wt, "mod.txt"), "changed\n");
        Git(wt, "add", "-A");
        Git(wt, "commit", "-m", "edit");

        var modified = await mgr.ReadWorktreeFileAsync(wt, "mod.txt", "main");
        Assert.NotNull(modified);
        Assert.Equal("modified", modified!.ChangeStatus);
        Assert.Contains("+changed", modified.Diff);
    }

    [Fact]
    public async Task ListWorktreeFiles_falls_back_to_origin_head_when_stored_branch_does_not_resolve()
    {
        var (work, mgr) = CloneWithOrigin();
        // origin/HEAD stays set (as after a fresh clone).
        var wt = await mgr.CreateWorktreeAsync(work, "feature-fallback");
        File.WriteAllText(Path.Combine(wt, "mod.txt"), "changed\n");
        Git(wt, "add", "-A");
        Git(wt, "commit", "-m", "edit");

        // A bogus stored branch can't resolve; the diff must fall back to
        // origin/HEAD instead of collapsing to empty.
        var files = await mgr.ListWorktreeFilesAsync(wt, "no-such-branch");
        Assert.Equal("modified", files.Single(f => f.Path == "mod.txt").ChangeStatus);
    }

    [Fact]
    public async Task InspectRemoteAsync_reads_default_branch_from_symref_and_name_from_url()
    {
        var dir = Path.Combine(_tmp, "ins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var origin = Path.Combine(dir, "inspect-proj.git");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-b", "develop");
        Git(origin, "config", "user.email", "t@t.io");
        Git(origin, "config", "user.name", "Tester");
        File.WriteAllText(Path.Combine(origin, "README.md"), "x\n");
        Git(origin, "add", "-A");
        Git(origin, "commit", "-m", "seed");

        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var info = await mgr.InspectRemoteAsync(origin);

        Assert.NotNull(info);
        Assert.Equal("develop", info!.DefaultBranch);
        Assert.Equal("inspect-proj", info.Name);
    }

    [Fact]
    public async Task InspectRemoteAsync_returns_null_for_unfetchable_remote()
    {
        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt"));
        var missing = Path.Combine(_tmp, "does-not-exist-" + Guid.NewGuid().ToString("N"));

        Assert.Null(await mgr.InspectRemoteAsync(missing));
    }

    [Fact]
    public async Task ReadWorktreeFile_flags_binary_and_blocks_path_traversal()
    {
        var (work, mgr) = CloneWithOrigin();
        var wt = await mgr.CreateWorktreeAsync(work, "feature-binary");

        // A NUL byte makes the file binary — content is withheld, and nothing
        // the viewer could draw comes back for an extension it can't render.
        File.WriteAllBytes(Path.Combine(wt, "blob.bin"), new byte[] { 1, 2, 0, 3, 4 });

        var binary = await mgr.ReadWorktreeFileAsync(wt, "blob.bin");
        Assert.NotNull(binary);
        Assert.True(binary!.IsBinary);
        Assert.Null(binary.Content);
        Assert.Null(binary.ImageMimeType);
        Assert.Null(binary.ImageBase64);

        Assert.Null(await mgr.ReadWorktreeFileAsync(wt, "../README.md"));
        Assert.Null(await mgr.ReadWorktreeFileAsync(wt, "does-not-exist.txt"));
    }

    [Fact]
    public async Task ReadWorktreeFile_inlines_a_renderable_image_as_base64()
    {
        var (work, mgr) = CloneWithOrigin();
        var wt = await mgr.CreateWorktreeAsync(work, "feature-image");

        var bytes = PngBytes();
        File.WriteAllBytes(Path.Combine(wt, "logo.PNG"), bytes);

        var image = await mgr.ReadWorktreeFileAsync(wt, "logo.PNG");
        Assert.NotNull(image);
        // Still binary with no text content — the bytes ride alongside.
        Assert.True(image!.IsBinary);
        Assert.Null(image.Content);
        Assert.Equal("image/png", image.ImageMimeType);
        Assert.Equal(Convert.ToBase64String(bytes), image.ImageBase64);
    }

    [Fact]
    public async Task ReadWorktreeFile_falls_back_to_the_binary_shape_for_an_oversized_image()
    {
        var (work, mgr) = CloneWithOrigin();
        var wt = await mgr.CreateWorktreeAsync(work, "feature-huge-image");

        // Past the inlining cap the read must degrade to the plain binary
        // response rather than either erroring or shipping the payload.
        var huge = new byte[(4 * 1024 * 1024) + 1];
        huge[0] = 0; // NUL — binary
        File.WriteAllBytes(Path.Combine(wt, "huge.png"), huge);

        var image = await mgr.ReadWorktreeFileAsync(wt, "huge.png");
        Assert.NotNull(image);
        Assert.True(image!.IsBinary);
        Assert.Null(image.Content);
        Assert.Null(image.ImageMimeType);
        Assert.Null(image.ImageBase64);
    }

    [Fact]
    public async Task ReadWorktreeFile_serves_svg_as_text_rather_than_as_an_image()
    {
        var (work, mgr) = CloneWithOrigin();
        var wt = await mgr.CreateWorktreeAsync(work, "feature-svg");

        // An SVG from a worktree is untrusted markup, so it is read as source
        // and never handed to the viewer as something to draw.
        const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>";
        File.WriteAllText(Path.Combine(wt, "icon.svg"), svg);

        var file = await mgr.ReadWorktreeFileAsync(wt, "icon.svg");
        Assert.NotNull(file);
        Assert.False(file!.IsBinary);
        Assert.Equal(svg, file.Content);
        Assert.Null(file.ImageMimeType);
        Assert.Null(file.ImageBase64);
    }

    /// <summary>A real 1x1 PNG — has the NUL bytes that make it read as binary.</summary>
    private static byte[] PngBytes() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    // Builds a repo with a real "origin" remote so origin/HEAD (the diff base)
    // resolves the way it does for a cloned-on-demand base repo in production.
    // ── Branch-sync primitives (the pull-branch path) ──────────────────────

    /// <summary>
    /// An origin plus a worktree on a run branch that has already been pushed to
    /// it — the shape <c>PullBranchAsync</c> operates on. The origin path is
    /// returned so a test can land a commit on the remote branch behind the
    /// worktree's back, which is exactly the case a pull exists to pick up.
    /// </summary>
    private async Task<(string Origin, string Worktree, RepositoryManager Mgr)> PushedRunBranchAsync(string branch)
    {
        var (work, mgr) = CloneWithOrigin();
        var origin = GitOutput(work, "remote", "get-url", "origin").Trim();
        var wt = await mgr.CreateWorktreeAsync(work, branch);
        Git(wt, "push", "-u", "origin", branch);
        return (origin, wt, mgr);
    }

    /// <summary>Commit <paramref name="content"/> to <paramref name="branch"/> in the origin repo.</summary>
    private static void CommitOnOrigin(string origin, string branch, string file, string content)
    {
        Git(origin, "checkout", branch);
        File.WriteAllText(Path.Combine(origin, file), content);
        Git(origin, "add", "-A");
        Git(origin, "commit", "-m", $"remote edit to {file}");
    }

    [Fact]
    public async Task Fetch_then_rebase_picks_up_commits_pushed_to_the_branch_after_it_was_created()
    {
        var (origin, wt, mgr) = await PushedRunBranchAsync("ild/wi-1-run-1");
        CommitOnOrigin(origin, "ild/wi-1-run-1", "mod.txt", "pushed by a human\n");

        Assert.True(await mgr.FetchAsync(wt));
        Assert.Equal(1, await mgr.GetCommitsBehindCountAsync(wt, "origin/ild/wi-1-run-1"));

        var rebase = await mgr.RebaseAsync(wt, "origin/ild/wi-1-run-1");

        Assert.True(rebase.Success);
        Assert.Empty(rebase.ConflictedFiles);
        Assert.Equal("pushed by a human\n", File.ReadAllText(Path.Combine(wt, "mod.txt")));
        Assert.Equal(0, await mgr.GetCommitsBehindCountAsync(wt, "origin/ild/wi-1-run-1"));
    }

    [Fact]
    public async Task RebaseAsync_aborts_a_conflicting_rebase_and_reports_the_conflicted_files()
    {
        var (origin, wt, mgr) = await PushedRunBranchAsync("ild/wi-2-run-1");

        // The run's own commit and a remote commit touch the same line.
        File.WriteAllText(Path.Combine(wt, "mod.txt"), "written by the agent\n");
        Git(wt, "add", "-A");
        Git(wt, "commit", "-m", "agent work");
        var localHead = GitOutput(wt, "rev-parse", "HEAD").Trim();
        CommitOnOrigin(origin, "ild/wi-2-run-1", "mod.txt", "written by a human\n");
        Assert.True(await mgr.FetchAsync(wt));

        var rebase = await mgr.RebaseAsync(wt, "origin/ild/wi-2-run-1");

        Assert.False(rebase.Success);
        Assert.Contains("mod.txt", rebase.ConflictedFiles);

        // The abort is the point: a worktree left mid-rebase has no usable branch
        // and would break every later node in the run.
        Assert.Equal(localHead, GitOutput(wt, "rev-parse", "HEAD").Trim());
        Assert.Equal("written by the agent\n", File.ReadAllText(Path.Combine(wt, "mod.txt")));
        Assert.Empty(GitOutput(wt, "status", "--porcelain").Trim());
        var rebaseState = GitOutput(wt, "rev-parse", "--git-path", "rebase-merge").Trim();
        Assert.False(Directory.Exists(Path.Combine(wt, rebaseState)));
    }

    /// <summary>
    /// Ends the rebase the way <see cref="ProcessRunner"/> does on cancellation —
    /// it kills the process tree and throws, rather than returning a failure code —
    /// and records every call with the token it was handed.
    /// </summary>
    private sealed class CancellingRebaseRunner : IProcessRunner
    {
        public List<(IReadOnlyList<string> Args, CancellationToken Token)> Calls { get; } = new();

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, string? workingDirectory = null, CancellationToken ct = default, IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            Calls.Add((args, ct));
            if (args.Count > 0 && args[0] == "rebase" && !args.Contains("--abort"))
                throw new OperationCanceledException(ct);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    [Fact]
    public async Task RebaseAsync_unwinds_before_letting_a_cancellation_propagate()
    {
        var runner = new CancellingRebaseRunner();
        var mgr = new RepositoryManager(runner, worktreesRoot: Path.Combine(_tmp, "wt"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => mgr.RebaseAsync(_repo, "origin/ild/wi-6-run-1", cts.Token));

        // Cancelling the run does not clean up after it: the worktree outlives the
        // run (ADR-0008), so a rebase killed mid-flight has to be unwound here or
        // it never is. The abort must carry an uncancelled token, or it would be
        // killed on arrival for the same reason the rebase was.
        var abort = runner.Calls.Single(c => c.Args.Contains("--abort"));
        Assert.False(abort.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Pull_path_authenticates_the_fetch_and_leaks_no_credential_to_the_rebase()
    {
        var runner = new RecordingRunner();
        var mgr = new RepositoryManager(runner, worktreesRoot: Path.Combine(_tmp, "wt"));
        var auth = new GitAuthOptions("https://git.kube/team/repo.git", "token-123", "Forgejo");

        await mgr.FetchAsync(_repo, auth: auth);
        await mgr.RebaseAsync(_repo, "origin/ild/wi-3-run-1");

        // The fetch is the only half of a pull that talks to the remote.
        Assert.Equal("token-123", runner.Calls[0].Environment!["ILD_GIT_PASSWORD"]);
        Assert.False(string.IsNullOrWhiteSpace(runner.Calls[0].Environment!["GIT_ASKPASS"]));
        // The rebase is local-only: no token, no askpass path, nothing for an
        // agent-authored git hook to read back out of the environment (ADR-0014).
        Assert.Contains("rebase", runner.Calls[1].Args);
        Assert.Null(runner.Calls[1].Environment);
    }

    [Fact]
    public async Task GetUncommittedFilesAsync_names_tracked_changes_and_ignores_untracked_files()
    {
        var (_, wt, mgr) = await PushedRunBranchAsync("ild/wi-4-run-1");

        Assert.Empty(await mgr.GetUncommittedFilesAsync(wt));

        File.WriteAllText(Path.Combine(wt, "mod.txt"), "edited\n");
        File.Delete(Path.Combine(wt, "gone.txt"));
        // Untracked scratch must not count as dirty: a rebase leaves it alone.
        File.WriteAllText(Path.Combine(wt, "scratch.log"), "noise\n");

        var dirty = await mgr.GetUncommittedFilesAsync(wt);

        Assert.Equal(new[] { "gone.txt", "mod.txt" }, dirty.OrderBy(f => f, StringComparer.Ordinal));
    }

    [Fact]
    public async Task RemoteBranchExistsAsync_reports_whether_the_branch_was_ever_pushed()
    {
        var (_, wt, mgr) = await PushedRunBranchAsync("ild/wi-5-run-1");

        Assert.True(await mgr.RemoteBranchExistsAsync(wt, "ild/wi-5-run-1"));
        Assert.False(await mgr.RemoteBranchExistsAsync(wt, "ild/wi-5-run-2"));
    }

    private (string Work, RepositoryManager Mgr) CloneWithOrigin()
    {
        var origin = Path.Combine(_tmp, "origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-b", "main");
        Git(origin, "config", "user.email", "t@t.io");
        Git(origin, "config", "user.name", "Tester");
        File.WriteAllText(Path.Combine(origin, "keep.txt"), "keep\n");
        File.WriteAllText(Path.Combine(origin, "mod.txt"), "original\n");
        File.WriteAllText(Path.Combine(origin, "gone.txt"), "doomed\n");
        Git(origin, "add", "-A");
        Git(origin, "commit", "-m", "seed");

        var work = Path.Combine(_tmp, "work-" + Guid.NewGuid().ToString("N"));
        Git(_tmp, "clone", origin, work);
        Git(work, "config", "user.email", "t@t.io");
        Git(work, "config", "user.name", "Tester");

        var mgr = new RepositoryManager(worktreesRoot: Path.Combine(_tmp, "wt-" + Guid.NewGuid().ToString("N")));
        return (work, mgr);
    }

    private static string GitOutput(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.StandardOutput.ReadToEnd();
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
    }
}
