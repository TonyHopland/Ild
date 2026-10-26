using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// clone_repository through the agent API, as the MCP server calls it: a chat
/// a schedule started asks ILD to clone or update a repository into its own
/// scratch folder. The remotes are local bare repositories standing behind
/// https clone URLs on a <c>.test</c> host, which only ILD's own base clone can
/// reach (the repository manager maps them), so a chat clone that went to the
/// remote itself instead of to ILD's base clone fails here as it would against
/// a private forge.
/// </summary>
public class ChatRepositoryCloneApiTests
{
    private const string ChatHeader = "X-ILD-Chat-Session-Id";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Regex SafeFolder = new("^[A-Za-z0-9_][A-Za-z0-9._-]*$");

    // -- Clone and update ---------------------------------------------------------

    [Fact]
    public async Task Two_repositories_land_in_separate_folders_on_their_default_branches_and_a_second_call_finds_each_again()
    {
        await using var host = await Host.StartAsync();
        var widgets = host.CreateRemote("widgets");
        var gadgets = host.CreateRemote("gadgets");
        var widgetsId = await host.SeedRepositoryAsync("widgets", widgets, defaultBranch: null);
        var gadgetsId = await host.SeedRepositoryAsync("gadgets", gadgets, defaultBranch: "develop");
        var scheduleId = await host.SeedScheduleAsync(ChatScheduleRepositoryScope.All);
        var (chat, scratch) = await host.SeedChatAsync(scheduleId);

        var first = await host.CloneAsync(chat, widgetsId);
        var second = await host.CloneAsync(chat, gadgetsId);

        first.AssertOk();
        Assert.Equal("Cloned", first.Outcome);
        Assert.Equal("main", first.Branch);
        Assert.Equal(widgets.Head("main"), first.Commit);
        Assert.Null(first.PreviousCommit);
        Assert.True(first.Changed);
        var widgetsFolder = AssertCheckout(scratch, first.Path, "main", widgets.Head("main"));
        Assert.Equal("widgets on main", File.ReadAllText(Path.Combine(widgetsFolder, "README.md")));

        second.AssertOk();
        Assert.Equal("Cloned", second.Outcome);
        Assert.Equal("develop", second.Branch);
        Assert.Equal(gadgets.Head("develop"), second.Commit);
        var gadgetsFolder = AssertCheckout(scratch, second.Path, "develop", gadgets.Head("develop"));
        Assert.NotEqual(widgetsFolder, gadgetsFolder);

        var again = await host.CloneAsync(chat, widgetsId);

        again.AssertOk();
        Assert.Equal("Updated", again.Outcome);
        Assert.Equal(first.Path, again.Path);
        Assert.Equal("main", again.Branch);
        Assert.Equal(first.Commit, again.Commit);
        Assert.Equal(first.Commit, again.PreviousCommit);
        Assert.False(again.Changed);

        AssertNoCredentials(widgetsFolder, widgets, host.ApiKey);
        AssertNoCredentials(gadgetsFolder, gadgets, host.ApiKey);

        // The credentialed half is ILD's own clone and fetch of the base clone runs
        // use, with the repository's provider credentials, and nothing more.
        var widgetsBase = BaseRepoPath.Fallback(widgetsId, host.Configuration);
        var gadgetsBase = BaseRepoPath.Fallback(gadgetsId, host.Configuration);
        var clones = host.Git.Calls.Where(c => c.Method == nameof(IRepositoryManager.CloneAsync)).ToList();
        Assert.Equal(new[] { widgetsBase, gadgetsBase }.Order(StringComparer.Ordinal), clones.Select(c => c.Path).Order(StringComparer.Ordinal));
        Assert.Contains(host.Git.Calls, c => c.Method == nameof(IRepositoryManager.FetchAsync) && c.Path == widgetsBase);
        Assert.All(host.Git.Calls.Where(c => c.Method is nameof(IRepositoryManager.CloneAsync) or nameof(IRepositoryManager.FetchAsync)), c =>
        {
            Assert.Equal(host.ApiKey, c.Auth?.ApiKey);
            Assert.Contains(c.Auth!.RemoteUrl, new[] { widgets.CloneUrl, gadgets.CloneUrl });
        });
        Assert.True(Directory.Exists(Path.Combine(widgetsBase, ".git")));
        AssertOrchestratorNeverRanGitIn(host, scratch);
    }

    [Fact]
    public async Task Folder_names_are_safe_on_disk_and_distinct_even_when_repository_names_clean_alike()
    {
        await using var host = await Host.StartAsync();
        var remote = host.CreateRemote("shared");
        var names = new[] { "My Repo", "My/Repo", "../-.", new string('a', 300) };
        var ids = new List<Guid>();
        foreach (var name in names)
            ids.Add(await host.SeedRepositoryAsync(name, remote, defaultBranch: null));
        var (chat, scratch) = await host.SeedChatAsync(await host.SeedScheduleAsync(ChatScheduleRepositoryScope.All));

        var paths = new List<string>();
        foreach (var id in ids)
        {
            var result = await host.CloneAsync(chat, id);
            result.AssertOk();
            Assert.Equal("Cloned", result.Outcome);
            AssertCheckout(scratch, result.Path, "main", remote.Head("main"));
            paths.Add(result.Path!);
        }

        Assert.Equal(paths.Count, paths.Distinct(StringComparer.Ordinal).Count());
        foreach (var path in paths)
        {
            var folder = Path.GetFileName(path);
            Assert.Matches(SafeFolder, folder);
            Assert.True(folder.Length <= 100, $"folder name of {folder.Length} characters");
        }
        Assert.Contains("Repo", Path.GetFileName(paths[0]), StringComparison.Ordinal);
        Assert.Contains("Repo", Path.GetFileName(paths[1]), StringComparison.Ordinal);
        Assert.Equal($"repos/My-Repo-{ids[1]:N}", paths[1]);

        var again = await host.CloneAsync(chat, ids[1]);

        again.AssertOk();
        Assert.Equal("Updated", again.Outcome);
        Assert.Equal(paths[1], again.Path);
        Assert.Equal(names.Length, Directory.GetDirectories(Path.Combine(scratch, "repos")).Length);
    }

    [Fact]
    public async Task An_update_follows_new_commits_and_branch_switches_without_handing_the_agent_credentials()
    {
        await using var host = await Host.StartAsync();
        var remote = host.CreateRemote("widgets");
        var repoId = await host.SeedRepositoryAsync("widgets", remote, defaultBranch: null);
        var (chat, scratch) = await host.SeedChatAsync(await host.SeedScheduleAsync(ChatScheduleRepositoryScope.All));
        var cloned = await host.CloneAsync(chat, repoId);
        cloned.AssertOk();
        var folder = Path.Combine(scratch, cloned.Path!);
        var dumps = host.InstallEnvironmentHooks(folder);

        var oldMain = remote.Head("main");
        var newMain = remote.Commit("main", "CHANGES.md", "a second commit");
        var updated = await host.CloneAsync(chat, repoId);

        updated.AssertOk();
        Assert.Equal("Updated", updated.Outcome);
        Assert.Equal(cloned.Path, updated.Path);
        Assert.Equal("main", updated.Branch);
        Assert.Equal(oldMain, updated.PreviousCommit);
        Assert.Equal(newMain, updated.Commit);
        Assert.True(updated.Changed);
        AssertCheckout(scratch, updated.Path, "main", newMain);
        Assert.True(File.Exists(Path.Combine(folder, "CHANGES.md")));

        var switched = await host.CloneAsync(chat, repoId, "feature");

        switched.AssertOk();
        Assert.Equal("Updated", switched.Outcome);
        Assert.Equal(cloned.Path, switched.Path);
        Assert.Equal("feature", switched.Branch);
        Assert.Equal(newMain, switched.PreviousCommit);
        Assert.Equal(remote.Head("feature"), switched.Commit);
        Assert.True(switched.Changed);
        AssertCheckout(scratch, switched.Path, "feature", remote.Head("feature"));
        Assert.Equal("origin/feature", Git(folder, "rev-parse", "--abbrev-ref", "feature@{upstream}"));

        var back = await host.CloneAsync(chat, repoId);

        back.AssertOk();
        Assert.Equal("main", back.Branch);
        Assert.Equal(newMain, back.Commit);
        Assert.True(back.Changed);
        AssertCheckout(scratch, back.Path, "main", newMain);

        AssertNoCredentials(folder, remote, host.ApiKey);
        foreach (var dump in Directory.GetFiles(dumps))
        {
            var environment = File.ReadAllText(dump);
            Assert.DoesNotContain(host.ApiKey, environment, StringComparison.Ordinal);
            foreach (var name in new[] { "GIT_ASKPASS", "ILD_GIT_PASSWORD", "ILD_GIT_USERNAME" })
                Assert.DoesNotMatch(new Regex($"(?m)^{name}="), environment);
        }
        AssertOrchestratorNeverRanGitIn(host, scratch);
    }

    public enum LocalWork
    {
        UncommittedChangeInTheWayOfTheUpdate,
        LocalCommitDivergingFromTheRemote,
        UncommittedChangeInTheWayOfTheSwitch,
        LocalCommitOnTheBranchSwitchedTo,
    }

    [Theory]
    [InlineData(LocalWork.UncommittedChangeInTheWayOfTheUpdate)]
    [InlineData(LocalWork.LocalCommitDivergingFromTheRemote)]
    [InlineData(LocalWork.UncommittedChangeInTheWayOfTheSwitch)]
    [InlineData(LocalWork.LocalCommitOnTheBranchSwitchedTo)]
    public async Task An_update_that_would_lose_local_work_is_refused_with_gits_message_changes_nothing_and_succeeds_once_reverted(LocalWork work)
    {
        await using var host = await Host.StartAsync();
        var remote = host.CreateRemote("widgets");
        var repoId = await host.SeedRepositoryAsync("widgets", remote, defaultBranch: null);
        var (chat, scratch) = await host.SeedChatAsync(await host.SeedScheduleAsync(ChatScheduleRepositoryScope.All));
        var cloned = await host.CloneAsync(chat, repoId);
        cloned.AssertOk();
        var folder = Path.Combine(scratch, cloned.Path!);

        string? branch;
        string expectedMessage;
        Action revert;
        switch (work)
        {
            case LocalWork.UncommittedChangeInTheWayOfTheUpdate:
                remote.Commit("main", "README.md", "upstream edit");
                File.WriteAllText(Path.Combine(folder, "README.md"), "agent edit");
                branch = null;
                expectedMessage = "would be overwritten";
                revert = () => Git(folder, "checkout", "--", "README.md");
                break;
            case LocalWork.LocalCommitDivergingFromTheRemote:
            {
                var before = Git(folder, "rev-parse", "HEAD");
                remote.Commit("main", "UPSTREAM.md", "upstream");
                File.WriteAllText(Path.Combine(folder, "LOCAL.md"), "local");
                Git(folder, "add", "LOCAL.md");
                Git(folder, "commit", "-q", "-m", "local work");
                branch = null;
                expectedMessage = "fast-forward";
                revert = () => Git(folder, "reset", "-q", "--hard", before);
                break;
            }
            case LocalWork.UncommittedChangeInTheWayOfTheSwitch:
                File.WriteAllText(Path.Combine(folder, "README.md"), "agent edit");
                branch = "feature";
                expectedMessage = "would be overwritten";
                revert = () => Git(folder, "checkout", "--", "README.md");
                break;
            case LocalWork.LocalCommitOnTheBranchSwitchedTo:
            {
                (await host.CloneAsync(chat, repoId, "feature")).AssertOk();
                (await host.CloneAsync(chat, repoId)).AssertOk();
                var before = Git(folder, "rev-parse", "refs/heads/feature");
                Git(folder, "checkout", "-q", "feature");
                File.WriteAllText(Path.Combine(folder, "LOCAL.md"), "local");
                Git(folder, "add", "LOCAL.md");
                Git(folder, "commit", "-q", "-m", "local work");
                Git(folder, "checkout", "-q", "main");
                remote.Commit("feature", "UPSTREAM.md", "upstream");
                branch = "feature";
                expectedMessage = "fast-forward";
                revert = () => Git(folder, "branch", "-f", "feature", before);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(work));
        }
        var untouched = Snapshot(folder);

        var refused = await host.CloneAsync(chat, repoId, branch);

        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal("UpdateRefused", refused.Outcome);
        Assert.Contains(expectedMessage, refused.Error, StringComparison.Ordinal);
        if (work is LocalWork.UncommittedChangeInTheWayOfTheUpdate or LocalWork.UncommittedChangeInTheWayOfTheSwitch)
            Assert.Contains("README.md", refused.Error, StringComparison.Ordinal);
        Assert.Equal(untouched, Snapshot(folder));

        revert();
        var retried = await host.CloneAsync(chat, repoId, branch);

        retried.AssertOk();
        Assert.Equal("Updated", retried.Outcome);
        var expectedBranch = branch ?? "main";
        Assert.Equal(expectedBranch, retried.Branch);
        AssertCheckout(scratch, retried.Path, expectedBranch, remote.Head(expectedBranch));
    }

    // -- Allowlist and refusals ---------------------------------------------------

    [Fact]
    public async Task The_allowlist_is_read_from_the_schedule_as_it_is_at_each_call()
    {
        await using var host = await Host.StartAsync();
        var remote = host.CreateRemote("shared");
        var listed = await host.SeedRepositoryAsync("listed", remote, defaultBranch: null);
        var unlisted = await host.SeedRepositoryAsync("unlisted", remote, defaultBranch: null);
        var scheduleId = await host.SeedScheduleAsync(ChatScheduleRepositoryScope.Selected, listed);
        var (chat, _) = await host.SeedChatAsync(scheduleId);

        (await host.CloneAsync(chat, listed)).AssertOk();
        var refused = await host.CloneAsync(chat, unlisted);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("NotAllowed", refused.Outcome);

        await host.SetScopeAsync(scheduleId, ChatScheduleRepositoryScope.All);
        var addedLater = await host.SeedRepositoryAsync("added later", remote, defaultBranch: null);
        (await host.CloneAsync(chat, unlisted)).AssertOk();
        (await host.CloneAsync(chat, addedLater)).AssertOk();

        await host.SetScopeAsync(scheduleId, ChatScheduleRepositoryScope.None);
        var none = await host.CloneAsync(chat, listed);
        Assert.Equal(HttpStatusCode.Forbidden, none.Status);
        Assert.Equal("NotAllowed", none.Outcome);

        await host.SetScopeAsync(scheduleId, ChatScheduleRepositoryScope.Selected, unlisted);
        (await host.CloneAsync(chat, unlisted)).AssertOk();
        Assert.Equal("NotAllowed", (await host.CloneAsync(chat, listed)).Outcome);
    }

    [Fact]
    public async Task Refusals_name_their_reason_and_write_nothing_to_the_chats_scratch_folder()
    {
        await using var host = await Host.StartAsync();
        var remote = host.CreateRemote("widgets");
        var repoId = await host.SeedRepositoryAsync("widgets", remote, defaultBranch: null);
        var otherId = await host.SeedRepositoryAsync("other", remote, defaultBranch: null);
        var unreachableId = await host.SeedRepositoryAsync("unreachable", host.UnreachableRemote("unreachable"), defaultBranch: null);
        var all = await host.SeedScheduleAsync(ChatScheduleRepositoryScope.All);
        var (scheduled, scheduledScratch) = await host.SeedChatAsync(all);
        var (ordinary, ordinaryScratch) = await host.SeedChatAsync(scheduleId: null);
        var doomed = await host.SeedScheduleAsync(ChatScheduleRepositoryScope.All);
        var (orphaned, orphanedScratch) = await host.SeedChatAsync(doomed);
        await host.DeleteScheduleAsync(doomed);
        var (scopeNone, scopeNoneScratch) = await host.SeedChatAsync(await host.SeedScheduleAsync(ChatScheduleRepositoryScope.None));
        var (selected, selectedScratch) = await host.SeedChatAsync(await host.SeedScheduleAsync(ChatScheduleRepositoryScope.Selected, otherId));

        // Refused on who is asking, what may be cloned or the branch's name alone:
        // no git runs at all.
        var beforeGit = new (string Case, Func<Task<CloneResult>> Call, HttpStatusCode Status, string? Outcome, string? Scratch)[]
        {
            ("no chat header", () => host.CloneAsync(chatHeader: null, repoId.ToString()), HttpStatusCode.BadRequest, null, null),
            ("malformed chat header", () => host.CloneAsync(chatHeader: "not-a-chat", repoId.ToString()), HttpStatusCode.BadRequest, null, null),
            ("unknown chat", () => host.CloneAsync(Guid.NewGuid(), repoId), HttpStatusCode.Forbidden, "UnknownChat", null),
            ("ordinary chat", () => host.CloneAsync(ordinary, repoId), HttpStatusCode.Forbidden, "NotScheduledChat", ordinaryScratch),
            ("chat of a deleted schedule", () => host.CloneAsync(orphaned, repoId), HttpStatusCode.Forbidden, "NotScheduledChat", orphanedScratch),
            ("scope None", () => host.CloneAsync(scopeNone, repoId), HttpStatusCode.Forbidden, "NotAllowed", scopeNoneScratch),
            ("not on the Selected list", () => host.CloneAsync(selected, repoId), HttpStatusCode.Forbidden, "NotAllowed", selectedScratch),
            ("repository id that is no GUID", () => host.CloneAsync(scheduled.ToString(), "widgets"), HttpStatusCode.NotFound, "UnknownRepository", scheduledScratch),
            ("unknown repository", () => host.CloneAsync(scheduled, Guid.NewGuid()), HttpStatusCode.NotFound, "UnknownRepository", scheduledScratch),
            ("invalid branch name", () => host.CloneAsync(scheduled, repoId, "bad..branch"), HttpStatusCode.BadRequest, "InvalidBranch", scheduledScratch),
        };
        foreach (var (name, call, status, outcome, scratch) in beforeGit)
        {
            var result = await call();
            Assert.True(status == result.Status, $"{name}: {result.Status} {result.Raw}");
            if (outcome is not null) Assert.True(outcome == result.Outcome, $"{name}: {result.Raw}");
            Assert.False(string.IsNullOrWhiteSpace(result.Error), $"{name}: {result.Raw}");
            if (scratch is not null) Assert.Empty(Directory.EnumerateFileSystemEntries(scratch));
            Assert.True(host.Git.Calls.IsEmpty, $"{name}: git ran ({string.Join(", ", host.Git.Calls.Select(c => c.Method))})");
        }

        var noSuchBranch = await host.CloneAsync(scheduled, repoId, "no-such-branch");
        Assert.Equal(HttpStatusCode.NotFound, noSuchBranch.Status);
        Assert.Equal("BranchNotFound", noSuchBranch.Outcome);
        Assert.Empty(Directory.EnumerateFileSystemEntries(scheduledScratch));

        var fetchFailed = await host.CloneAsync(scheduled, unreachableId);
        Assert.Equal(HttpStatusCode.BadGateway, fetchFailed.Status);
        Assert.Equal("FetchFailed", fetchFailed.Outcome);
        Assert.Contains("fetch", fetchFailed.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(scheduledScratch));
        Assert.False(Directory.Exists(BaseRepoPath.Fallback(unreachableId, host.Configuration)),
            "a failed fresh base clone must leave no folder behind");
    }

    // -- Concurrency and cancellation ---------------------------------------------

    [Fact]
    public async Task Parallel_calls_for_one_chat_never_share_a_folder_or_overlap()
    {
        await using var host = await Host.StartAsync();
        var remote = host.CreateRemote("shared");
        var spaced = await host.SeedRepositoryAsync("My Repo", remote, defaultBranch: null);
        var slashed = await host.SeedRepositoryAsync("My/Repo", host.CreateRemote("other"), defaultBranch: null);
        var (chat, scratch) = await host.SeedChatAsync(await host.SeedScheduleAsync(ChatScheduleRepositoryScope.All));
        var gate = host.Git.Arm();

        var first = host.CloneAsync(chat, spaced);
        await WaitUntilHeldAsync(gate, first);
        var repeat = host.CloneAsync(chat, spaced);
        var lookalike = host.CloneAsync(chat, slashed);
        gate.Release();
        var results = await Task.WhenAll(first, repeat, lookalike);

        Assert.All(results, r => r.AssertOk());
        Assert.Equal("Cloned", results[0].Outcome);
        Assert.Equal("Updated", results[1].Outcome);
        Assert.False(results[1].Changed);
        Assert.Equal(results[0].Path, results[1].Path);
        Assert.NotEqual(results[0].Path, results[2].Path);
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(scratch, "repos")).Length);
        Assert.Equal(remote.CloneUrlWithoutUserInfo, Git(Path.Combine(scratch, results[0].Path!), "config", "--get", "remote.origin.url"));
        var spacedBase = BaseRepoPath.Fallback(spaced, host.Configuration);
        Assert.Single(host.Git.Calls, c => c.Method == nameof(IRepositoryManager.CloneAsync) && c.Path == spacedBase);
        Assert.Equal(1, gate.MostAtOnceFor(spacedBase));
    }

    [Fact]
    public async Task A_cancelled_request_leaves_the_clone_it_started_to_finish_and_a_later_call_picks_it_up()
    {
        await using var host = await Host.StartAsync();
        var remote = host.CreateRemote("widgets");
        var repoId = await host.SeedRepositoryAsync("widgets", remote, defaultBranch: null);
        var (chat, scratch) = await host.SeedChatAsync(await host.SeedScheduleAsync(ChatScheduleRepositoryScope.All));
        var gate = host.Git.Arm();
        using var agentGivesUp = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var abandoned = host.CloneAsync(chat, repoId, branch: null, agentGivesUp.Token);
        await WaitUntilHeldAsync(gate, abandoned);
        agentGivesUp.Cancel();
        gate.Release();
        try { await abandoned; } catch (OperationCanceledException) { }

        var later = await host.CloneAsync(chat, repoId);

        later.AssertOk();
        Assert.Equal("Updated", later.Outcome);
        Assert.False(later.Changed);
        AssertCheckout(scratch, later.Path, "main", remote.Head("main"));
        Assert.Single(host.Git.Calls, c => c.Method == nameof(IRepositoryManager.CloneAsync));
        Assert.True(Directory.Exists(Path.Combine(BaseRepoPath.Fallback(repoId, host.Configuration), ".git")));
    }

    private static async Task WaitUntilHeldAsync(BaseGitGate gate, Task<CloneResult> call)
    {
        if (await Task.WhenAny(gate.Entered, call).WaitAsync(Ct) == call)
        {
            var result = await call;
            Assert.Fail($"the call ended before ILD fetched the repository: {result.Status} {result.Raw}");
        }
    }

    // -- Assertions ---------------------------------------------------------------

    /// <summary>The clone the result names: its own folder under the chat's repos folder, on <paramref name="branch"/> at <paramref name="commit"/>, clean.</summary>
    private static string AssertCheckout(string scratch, string? relativePath, string branch, string commit)
    {
        Assert.False(string.IsNullOrEmpty(relativePath));
        Assert.False(Path.IsPathRooted(relativePath), relativePath);
        var folder = Path.GetFullPath(Path.Combine(scratch, relativePath));
        Assert.Equal(Path.Combine(Path.GetFullPath(scratch), "repos"), Path.GetDirectoryName(folder));
        Assert.True(Directory.Exists(Path.Combine(folder, ".git")), $"no clone at {folder}");
        Assert.Equal(branch, Git(folder, "symbolic-ref", "--short", "HEAD"));
        Assert.Equal(commit, Git(folder, "rev-parse", "HEAD"));
        Assert.Equal("", Git(folder, "status", "--porcelain"));
        Assert.False(File.Exists(Path.Combine(scratch, "README.md")), "something was checked out into the scratch root");
        return folder;
    }

    private static void AssertNoCredentials(string folder, Remote remote, string apiKey)
    {
        Assert.Equal(remote.CloneUrlWithoutUserInfo, Git(folder, "config", "--get", "remote.origin.url"));
        Assert.Equal("", GitAllowingFailure(folder, "config", "--get-regexp", "^credential"));
        var key = Encoding.UTF8.GetBytes(apiKey);
        foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            Assert.True(File.ReadAllBytes(file).AsSpan().IndexOf(key) < 0, $"the API key is in {file}");
    }

    private static void AssertOrchestratorNeverRanGitIn(Host host, string scratch)
        => Assert.All(host.Git.Calls, c => Assert.All(c.Arguments, a => Assert.False(
            a.StartsWith(Path.GetFullPath(scratch), StringComparison.Ordinal), $"{c.Method} ran on {a}")));

    /// <summary>What a refused update must leave alone: HEAD, the branch, every local branch, the index and the working tree.</summary>
    private static string Snapshot(string folder) => string.Join("\n---\n",
        Git(folder, "rev-parse", "HEAD"),
        GitAllowingFailure(folder, "symbolic-ref", "-q", "HEAD"),
        Git(folder, "for-each-ref", "--format=%(refname) %(objectname)", "refs/heads"),
        Git(folder, "ls-files", "--stage"),
        Git(folder, "status", "--porcelain=v1", "--untracked-files=all"),
        Git(folder, "diff"),
        string.Join("\n", Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal)
            .Select(f => $"{Path.GetFileName(f)}={File.ReadAllText(f)}")));

    // -- Git ----------------------------------------------------------------------

    private static readonly string[] Identity =
        ["-c", "user.name=Test", "-c", "user.email=test@example.test", "-c", "commit.gpgsign=false", "-c", "init.defaultBranch=main"];

    private static string Git(string folder, params string[] args)
    {
        var (code, stdout, stderr) = RunGit(folder, args);
        if (code != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} in {folder}: {stderr}");
        return stdout.Trim();
    }

    private static string GitAllowingFailure(string folder, params string[] args) => RunGit(folder, args).Stdout.Trim();

    private static (int Code, string Stdout, string Stderr) RunGit(string folder, string[] args)
    {
        var psi = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in Identity) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(folder);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr.Result);
    }

    /// <summary>
    /// A forge repository: a local bare repository with <c>main</c>, <c>develop</c>
    /// and <c>feature</c>, which ILD knows by an https clone URL that carries a
    /// username, as configured clone URLs may.
    /// </summary>
    private sealed class Remote
    {
        private readonly string _work;

        public string Bare { get; }
        public string CloneUrl { get; }
        public string CloneUrlWithoutUserInfo { get; }

        public Remote(string root, string name)
        {
            Bare = Path.Combine(root, "remotes", name + ".git");
            _work = Path.Combine(root, "remotes", name + "-work");
            CloneUrl = $"https://ild-bot@forge.example.test/team/{name}.git";
            CloneUrlWithoutUserInfo = $"https://forge.example.test/team/{name}.git";
            Directory.CreateDirectory(Bare);
            Git(Bare, "init", "-q", "--bare", "-b", "main");
            Git(root, "clone", "-q", Bare, _work);
            File.WriteAllText(Path.Combine(_work, "README.md"), $"{name} on main");
            Git(_work, "add", "README.md");
            Git(_work, "commit", "-q", "-m", "first");
            Git(_work, "push", "-q", "origin", "main");
            Git(_work, "checkout", "-q", "-b", "develop");
            Commit("develop", "DEVELOP.md", "develop only");
            Git(_work, "checkout", "-q", "-b", "feature", "main");
            Commit("feature", "README.md", $"{name} on feature");
        }

        /// <summary>Push a commit to <paramref name="branch"/> and return its SHA.</summary>
        public string Commit(string branch, string file, string content)
        {
            Git(_work, "checkout", "-q", branch);
            File.WriteAllText(Path.Combine(_work, file), content);
            Git(_work, "add", file);
            Git(_work, "commit", "-q", "-m", $"{file} on {branch}");
            Git(_work, "push", "-q", "origin", branch);
            return Git(_work, "rev-parse", "HEAD");
        }

        public string Head(string branch) => Git(Bare, "rev-parse", $"refs/heads/{branch}");
    }

    // -- Host ---------------------------------------------------------------------

    private sealed record CloneResult(HttpStatusCode Status, string Raw, JsonElement? Body)
    {
        private string? Text(string name)
            => Body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        public string? Outcome => Text("outcome");
        public string? Path => Text("path");
        public string? Branch => Text("branch");
        public string? Commit => Text("commit");
        public string? PreviousCommit => Text("previousCommit");
        public string? Error => Text("error");
        public bool Changed => Body!.Value.GetProperty("changed").GetBoolean();

        public void AssertOk() => Assert.True(Status == HttpStatusCode.OK, $"{Status}: {Raw}");
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ild-clone-tests", Guid.NewGuid().ToString("N"));
        private Guid _providerId;

        public ApiFactory Factory { get; }
        public RecordingRepositoryManager Git { get; }
        public string ApiKey { get; } = "ild-clone-key-" + Guid.NewGuid().ToString("N");
        public IConfiguration Configuration => Factory.Services.GetRequiredService<IConfiguration>();

        private Host()
        {
            Directory.CreateDirectory(_root);
            Git = RecordingRepositoryManager.Around(new RepositoryManager(new ProcessRunner(), null, Path.Combine(_root, "worktrees"), null, Path.Combine(_root, "mirrors")));
            var database = SqliteDatabaseFile.Create(Path.Combine(_root, "db"));
            Factory = new ApiFactory(configureServices: services =>
            {
                SqliteDatabaseFile.Use(services, database);
                services.ReplaceSingleton<IRepositoryManager>((IRepositoryManager)(object)Git);
            });
        }

        public static async Task<Host> StartAsync()
        {
            var host = new Host();
            using var scope = host.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var provider = new RemoteProvider
            {
                Id = Guid.NewGuid(),
                Name = "forge",
                Type = "forgejo",
                Url = "https://forge.example.test",
                ApiKey = host.ApiKey,
                CreatedAt = DateTime.UtcNow,
            };
            db.RemoteProviders.Add(provider);
            await db.SaveChangesAsync(Ct);
            host._providerId = provider.Id;
            return host;
        }

        public Remote CreateRemote(string name)
        {
            var remote = new Remote(_root, name);
            Git.Urls[remote.CloneUrl] = remote.Bare;
            return remote;
        }

        /// <summary>A remote ILD cannot fetch from: its clone URL leads nowhere.</summary>
        public Remote UnreachableRemote(string name)
        {
            var remote = new Remote(_root, name);
            Git.Urls[remote.CloneUrl] = Path.Combine(_root, "nowhere", name + ".git");
            return remote;
        }

        public async Task<Guid> SeedRepositoryAsync(string name, Remote remote, string? defaultBranch)
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repo = new Repository
            {
                Id = Guid.NewGuid(),
                Name = name,
                RemoteProviderId = _providerId,
                CloneUrl = remote.CloneUrl,
                DefaultBranch = defaultBranch,
                CreatedAt = DateTime.UtcNow,
            };
            db.Repositories.Add(repo);
            await db.SaveChangesAsync(Ct);
            return repo.Id;
        }

        public async Task<Guid> SeedScheduleAsync(ChatScheduleRepositoryScope scope, params Guid[] repositoryIds)
        {
            using var services = Factory.Services.CreateScope();
            var db = services.ServiceProvider.GetRequiredService<AppDbContext>();
            var schedule = new ChatSchedule
            {
                Id = Guid.NewGuid(),
                UserId = "scheduler",
                Name = "nightly check",
                Prompt = "Check the dependencies.",
                CronExpression = "0 3 * * *",
                TimeZone = "UTC",
                RepositoryScope = scope,
                RepositoryIdsCsv = string.Join(',', repositoryIds),
                CreatedAt = DateTime.UtcNow,
            };
            db.ChatSchedules.Add(schedule);
            await db.SaveChangesAsync(Ct);
            return schedule.Id;
        }

        public async Task SetScopeAsync(Guid scheduleId, ChatScheduleRepositoryScope scope, params Guid[] repositoryIds)
        {
            using var services = Factory.Services.CreateScope();
            var db = services.ServiceProvider.GetRequiredService<AppDbContext>();
            var schedule = await db.ChatSchedules.SingleAsync(s => s.Id == scheduleId, Ct);
            schedule.RepositoryScope = scope;
            schedule.RepositoryIdsCsv = string.Join(',', repositoryIds);
            await db.SaveChangesAsync(Ct);
        }

        public async Task DeleteScheduleAsync(Guid scheduleId)
        {
            using var services = Factory.Services.CreateScope();
            var db = services.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.ChatSessions.Where(c => c.ScheduleId == scheduleId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ScheduleId, (Guid?)null), Ct);
            await db.ChatSchedules.Where(s => s.Id == scheduleId).ExecuteDeleteAsync(Ct);
        }

        public async Task<(Guid Id, string Scratch)> SeedChatAsync(Guid? scheduleId)
        {
            using var services = Factory.Services.CreateScope();
            var db = services.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = new ChatSession
            {
                Id = Guid.NewGuid(),
                UserId = "scheduler",
                AiProviderId = Guid.NewGuid(),
                ProviderType = "claude-code",
                ToolAllowlistCsv = "ild",
                ScratchPath = Path.Combine(_root, "scratch", Guid.NewGuid().ToString("N")),
                ScheduleId = scheduleId,
                CreatedAt = DateTime.UtcNow,
            };
            Directory.CreateDirectory(session.ScratchPath);
            db.ChatSessions.Add(session);
            await db.SaveChangesAsync(Ct);
            return (session.Id, session.ScratchPath);
        }

        /// <summary>
        /// Hooks the agent may install in its clone, recording the environment of
        /// every git command that then updates a ref, checks out or merges there.
        /// </summary>
        public string InstallEnvironmentHooks(string clone)
        {
            var dumps = Path.Combine(_root, "hook-environments", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dumps);
            foreach (var hook in new[] { "reference-transaction", "post-checkout", "post-merge" })
            {
                var path = Path.Combine(clone, ".git", "hooks", hook);
                File.WriteAllText(path, $"#!/bin/sh\nenv > '{dumps}/{hook}-'$$.env\n");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return dumps;
        }

        public Task<CloneResult> CloneAsync(Guid chat, Guid repositoryId, string? branch = null, CancellationToken? ct = null)
            => CloneAsync(chat.ToString(), repositoryId.ToString(), branch, ct);

        public async Task<CloneResult> CloneAsync(string? chatHeader, string repositoryId, string? branch = null, CancellationToken? ct = null)
        {
            using var client = Factory.CreateClient();
            var token = Factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (chatHeader is not null) client.DefaultRequestHeaders.Add(ChatHeader, chatHeader);

            var response = await client.PostAsJsonAsync(
                $"/api/v1/agent/chat/repositories/{Uri.EscapeDataString(repositoryId)}/clone", new { branch }, ct ?? Ct);
            var raw = await response.Content.ReadAsStringAsync(Ct);
            JsonElement? body = null;
            try { body = JsonDocument.Parse(raw).RootElement.Clone(); } catch (JsonException) { }
            return new CloneResult(response.StatusCode, raw, body);
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>A gate on ILD's base clone and fetch: every one waits until released.</summary>
    public sealed class BaseGitGate
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, int> _inFlight = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _most = new(StringComparer.Ordinal);

        public Task Entered => _entered.Task;

        public void Release() => _released.TrySetResult();

        public int MostAtOnceFor(string path) => _most.GetValueOrDefault(path);

        internal async Task<T> PassAsync<T>(string path, Func<Task<T>> operation)
        {
            var now = _inFlight.AddOrUpdate(path, 1, (_, n) => n + 1);
            _most.AddOrUpdate(path, now, (_, m) => Math.Max(m, now));
            try
            {
                _entered.TrySetResult();
                await _released.Task;
                return await operation();
            }
            finally
            {
                _inFlight.AddOrUpdate(path, 0, (_, n) => n - 1);
            }
        }
    }

    /// <summary>
    /// ILD's real repository manager, recording every call, standing the local
    /// bare repositories in for the clone URLs ILD knows, and optionally holding
    /// the base clone and fetch at a <see cref="BaseGitGate"/>.
    /// </summary>
    public class RecordingRepositoryManager : DispatchProxy
    {
        private IRepositoryManager _inner = null!;
        private BaseGitGate? _gate;

        public ConcurrentQueue<(string Method, string? Path, GitAuthOptions? Auth, string[] Arguments)> Calls { get; } = new();
        public ConcurrentDictionary<string, string> Urls { get; } = new(StringComparer.Ordinal);

        public static RecordingRepositoryManager Around(IRepositoryManager inner)
        {
            var proxy = (RecordingRepositoryManager)(object)Create<IRepositoryManager, RecordingRepositoryManager>();
            proxy._inner = inner;
            return proxy;
        }

        public BaseGitGate Arm() => _gate = new BaseGitGate();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            args ??= [];
            var parameters = method!.GetParameters();
            var strings = args.OfType<string>().ToArray();
            var auth = args.OfType<GitAuthOptions>().FirstOrDefault();

            if (parameters.Length > 0 && parameters[0].Name == "cloneUrl" && args[0] is string url && Urls.TryGetValue(url, out var local))
                args[0] = local;

            switch (method.Name)
            {
                case nameof(IRepositoryManager.CloneAsync):
                    Calls.Enqueue((method.Name, (string)args[1]!, auth, strings));
                    return Gated((string)args[1]!, () => _inner.CloneAsync((string)args[0]!, (string)args[1]!, (CancellationToken)args[2]!, auth));
                case nameof(IRepositoryManager.FetchAsync):
                    Calls.Enqueue((method.Name, (string)args[0]!, auth, strings));
                    // The fetch goes to the URL it is given, so the stand-in remote is substituted as for a clone.
                    var reachable = auth is not null && Urls.TryGetValue(auth.RemoteUrl, out var bare) ? auth with { RemoteUrl = bare } : auth;
                    return Gated((string)args[0]!, () => _inner.FetchAsync((string)args[0]!, (CancellationToken)args[1]!, reachable));
            }

            Calls.Enqueue((method.Name, strings.FirstOrDefault(), auth, strings));
            try
            {
                return method.Invoke(_inner, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(e.InnerException);
                throw;
            }
        }

        private Task<T> Gated<T>(string path, Func<Task<T>> operation)
            => _gate is { } gate ? gate.PassAsync(path, operation) : operation();
    }
}
