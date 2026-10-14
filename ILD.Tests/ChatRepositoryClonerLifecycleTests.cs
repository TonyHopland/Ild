using System.Diagnostics;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The chat's clone over its life: what becomes of clone work whose chat goes
/// away, what the cloner keeps in memory per chat, how a clone is found again,
/// and what an agent-installed hook can do to an update.
/// </summary>
public sealed class ChatRepositoryClonerLifecycleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ild-clone-lifecycle-").FullName;
    private readonly TestDb _db = new();
    private readonly ChatCloneCoordinator _chatClones = new();
    private readonly ServiceProvider _services;

    public ChatRepositoryClonerLifecycleTests()
    {
        _services = new ServiceCollection().AddScoped(_ => _db.OnOwnConnection()).BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
        if (OperatingSystem.IsLinux())
        {
            foreach (var dir in Directory.EnumerateDirectories(_dir, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IConfiguration Config => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:DataPath"] = Path.Combine(_dir, "data") })
        .Build();

    private RepositoryManager RealGit => new(worktreesRoot: Path.Combine(_dir, "worktrees"));

    private ChatRepositoryCloner Cloner(IRepositoryManager? git = null, IProviderStore? providers = null)
        => new(_db.Context, _services.GetRequiredService<IServiceScopeFactory>(), _chatClones, providers ?? _db.Providers,
            git ?? RealGit, Config, ShutdownState.NeverStopping);

    // -- Chat deletion and per-chat memory ------------------------------------------

    [Fact]
    public async Task Deleting_the_chat_stops_its_clone_and_the_scratch_folder_stays_gone()
    {
        var (chatId, scratch, repo) = await SeedScheduledChatAsync(CreateRemote());
        var real = RealGit;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var git = new Mock<IRepositoryManager>();
        git.Setup(g => g.CloneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .Returns(async (string url, string path, CancellationToken token, GitAuthOptions? _) =>
            {
                entered.TrySetResult();
                await released.Task.WaitAsync(token);
                return await real.CloneAsync(url, path, token);
            });
        git.Setup(g => g.RemoteBranchExistsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string path, string branch) => real.RemoteBranchExistsAsync(path, branch));
        var cloner = Cloner(git.Object);

        var call = cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, Ct);
        await entered.Task.WaitAsync(Ct);

        await DeleteChatAsync(chatId, scratch);
        released.TrySetResult();
        var result = await call;

        Assert.Equal(ChatRepositoryCloneOutcome.UnknownChat, result.Outcome);
        Assert.False(Directory.Exists(scratch), "the clone brought the deleted chat's scratch folder back");
        Assert.False(_chatClones.IsTracked(chatId), "the deleted chat's entry stayed in memory");
    }

    [Fact]
    public async Task A_call_that_checked_the_chat_just_before_it_was_deleted_is_refused_and_leaves_nothing_behind()
    {
        var (chatId, scratch, repo) = await SeedScheduledChatAsync(CreateRemote());
        var lookedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providers = new Mock<IProviderStore>();
        providers.Setup(p => p.GetRepositoryByIdAsync(repo.Id)).Returns(async () =>
        {
            lookedUp.TrySetResult();
            await deleted.Task;
            return await _db.Providers.GetRepositoryByIdAsync(repo.Id);
        });
        providers.Setup(p => p.GetRemoteProviderByIdAsync(It.IsAny<Guid>()))
            .Returns((Guid id) => _db.Providers.GetRemoteProviderByIdAsync(id));

        // The call has found the chat and is between that check and the chat's lock
        // while the chat is deleted, as ChatService deletes it.
        var call = Cloner(providers: providers.Object).CloneAsync(chatId, repo.Id.ToString(), branch: null, Ct);
        await lookedUp.Task.WaitAsync(Ct);
        await DeleteChatAsync(chatId, scratch);
        deleted.TrySetResult();
        var result = await call;

        Assert.Equal(ChatRepositoryCloneOutcome.UnknownChat, result.Outcome);
        Assert.False(Directory.Exists(scratch), "the clone brought the deleted chat's scratch folder back");
        Assert.False(_chatClones.IsTracked(chatId));
    }

    [Fact]
    public async Task Finished_calls_leave_nothing_of_the_chat_in_memory()
    {
        var (chatId, _, repo) = await SeedScheduledChatAsync(CreateRemote());
        var cloner = Cloner();

        var cloned = await cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, Ct);
        var refused = await cloner.CloneAsync(chatId, repo.Id.ToString(), "no-such-branch", Ct);
        var updated = await cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, Ct);

        Assert.Equal(ChatRepositoryCloneOutcome.Cloned, cloned.Outcome);
        Assert.Equal(ChatRepositoryCloneOutcome.BranchNotFound, refused.Outcome);
        Assert.Equal(ChatRepositoryCloneOutcome.Updated, updated.Outcome);
        Assert.False(_chatClones.IsTracked(chatId));
        Assert.False(BaseRepoClone.IsTracked(repo.Id));
    }

    [Fact]
    public async Task The_repository_lock_is_dropped_once_released_and_by_a_waiter_that_gives_up()
    {
        var repositoryId = Guid.NewGuid();
        var held = await BaseRepoClone.LockAsync(repositoryId, Ct);
        using var givesUp = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var waiter = BaseRepoClone.LockAsync(repositoryId, givesUp.Token);

        Assert.False(waiter.IsCompleted);
        await givesUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.True(BaseRepoClone.IsTracked(repositoryId));

        held.Dispose();
        held.Dispose();

        Assert.False(BaseRepoClone.IsTracked(repositoryId));
        using (await BaseRepoClone.LockAsync(repositoryId, Ct))
            Assert.True(BaseRepoClone.IsTracked(repositoryId));
        Assert.False(BaseRepoClone.IsTracked(repositoryId));
    }

    // -- Finding the clone again -----------------------------------------------------

    [Fact]
    public async Task A_renamed_repository_is_updated_in_the_folder_it_was_cloned_into()
    {
        var (chatId, scratch, repo) = await SeedScheduledChatAsync(CreateRemote());
        var cloner = Cloner();
        var cloned = await cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, Ct);
        File.WriteAllText(Path.Combine(scratch, cloned.Path!, "NOTES.md"), "the agent's own work");

        repo.Name = "gadgets";
        await _db.Context.SaveChangesAsync(Ct);
        var again = await cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, Ct);

        Assert.Equal(ChatRepositoryCloneOutcome.Updated, again.Outcome);
        Assert.Equal(cloned.Path, again.Path);
        Assert.Equal(["widgets"], Directory.GetDirectories(Path.Combine(scratch, "repos")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Copies_and_worktrees_of_the_clone_are_not_taken_for_it()
    {
        var (chatId, scratch, repo) = await SeedScheduledChatAsync(CreateRemote());
        var cloner = Cloner();
        var cloned = await cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, Ct);
        var repos = Path.Combine(scratch, "repos");
        Git(Path.Combine(repos, "widgets"), "worktree", "add", "-q", "-b", "bugfix", Path.Combine(repos, "aaa-bugfix"));

        var withWorktree = await cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, Ct);
        repo.Name = "gadgets";
        await _db.Context.SaveChangesAsync(Ct);
        var renamed = await cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, Ct);
        repo.Name = "widgets";
        await _db.Context.SaveChangesAsync(Ct);
        CopyDirectory(Path.Combine(repos, "widgets"), Path.Combine(repos, "backup-widgets"));
        var withCopy = await cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, Ct);

        Assert.All([withWorktree, renamed, withCopy], r =>
        {
            Assert.Equal(ChatRepositoryCloneOutcome.Updated, r.Outcome);
            Assert.Equal(cloned.Path, r.Path);
        });
    }

    [Fact]
    public async Task A_second_repository_that_cleans_to_the_same_name_gets_a_folder_named_after_its_id()
    {
        var remote = CreateRemote();
        var (chatId, _, repo) = await SeedScheduledChatAsync(remote);
        var lookalike = new Repository
        {
            Id = Guid.NewGuid(), Name = "widgets!", RemoteProviderId = repo.RemoteProviderId, CloneUrl = remote, CreatedAt = DateTime.UtcNow,
        };
        _db.Context.Repositories.Add(lookalike);
        await _db.Context.SaveChangesAsync(Ct);
        var cloner = Cloner();

        var first = await cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, Ct);
        var second = await cloner.CloneAsync(chatId, lookalike.Id.ToString(), branch: null, Ct);
        var again = await cloner.CloneAsync(chatId, lookalike.Id.ToString(), branch: null, Ct);

        Assert.Equal("repos/widgets", first.Path);
        Assert.Equal(ChatRepositoryCloneOutcome.Cloned, second.Outcome);
        Assert.Equal($"repos/widgets-{lookalike.Id:N}", second.Path);
        Assert.Equal(ChatRepositoryCloneOutcome.Updated, again.Outcome);
        Assert.Equal(second.Path, again.Path);
    }

    // -- Branches --------------------------------------------------------------------

    [Fact]
    public async Task A_branch_with_local_commits_ahead_of_its_remote_is_switched_back_to_as_it_is()
    {
        var (chatId, scratch, repo) = await SeedScheduledChatAsync(CreateRemote());
        var cloner = Cloner();
        var cloned = await cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, Ct);
        var folder = Path.Combine(scratch, cloned.Path!);
        File.WriteAllText(Path.Combine(folder, "LOCAL.md"), "the agent's own commit");
        Git(folder, "add", "LOCAL.md");
        Git(folder, "commit", "-q", "-m", "local work");
        var local = Git(folder, "rev-parse", "HEAD");

        var switched = await cloner.CloneAsync(chatId, repo.Id.ToString(), "feature", Ct);
        var back = await cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, Ct);

        Assert.Equal(ChatRepositoryCloneOutcome.Updated, switched.Outcome);
        Assert.Equal(ChatRepositoryCloneOutcome.Updated, back.Outcome);
        Assert.Equal("main", back.Branch);
        Assert.Equal(local, back.Commit);
        Assert.Equal(local, Git(folder, "rev-parse", "refs/heads/main"));
        Assert.True(File.Exists(Path.Combine(folder, "LOCAL.md")));
    }

    // -- Hooks -------------------------------------------------------------------------

    [Fact]
    public async Task A_failing_hook_in_the_clone_neither_runs_nor_turns_a_switch_into_a_refusal()
    {
        var (chatId, scratch, repo) = await SeedScheduledChatAsync(CreateRemote());
        var cloner = Cloner();
        var cloned = await cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, Ct);
        var folder = Path.Combine(scratch, cloned.Path!);
        var ran = Path.Combine(_dir, "hook-ran");
        foreach (var hook in new[] { "post-checkout", "post-merge", "reference-transaction" })
        {
            var path = Path.Combine(folder, ".git", "hooks", hook);
            File.WriteAllText(path, $"#!/bin/sh\ntouch '{ran}'\nexit 1\n");
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var switched = await cloner.CloneAsync(chatId, repo.Id.ToString(), "feature", Ct);
        var back = await cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, Ct);
        PushCommit("main", "CHANGES.md");
        var merged = await cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, Ct);

        Assert.Equal(ChatRepositoryCloneOutcome.Updated, switched.Outcome);
        Assert.Equal("feature", switched.Branch);
        Assert.Equal(ChatRepositoryCloneOutcome.Updated, back.Outcome);
        Assert.Equal("main", back.Branch);
        Assert.Equal(ChatRepositoryCloneOutcome.Updated, merged.Outcome);
        Assert.True(merged.Changed);
        Assert.False(File.Exists(ran), "a hook in the agent's clone ran");
    }

    // -- Base clone --------------------------------------------------------------------

    [Fact]
    public async Task A_failed_fresh_base_clone_whose_folder_cannot_be_removed_still_reports_the_failure()
    {
        // A read-only folder stops only a user who is not root.
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root") return;

        var repo = new Repository { Id = Guid.NewGuid(), Name = "widgets", CloneUrl = "https://forge.example.test/team/widgets.git" };
        var target = BaseRepoPath.Fallback(repo.Id, Config);
        var git = new Mock<IRepositoryManager>();
        git.Setup(g => g.CloneAsync(repo.CloneUrl, target, It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .Returns((string _, string path, CancellationToken _, GitAuthOptions? _) =>
            {
                Directory.CreateDirectory(Path.Combine(path, ".git", "objects"));
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                return Task.FromResult<(bool, string?)>((false, "fatal: the remote hung up unexpectedly"));
            });

        var result = await BaseRepoClone.EnsureAsync(git.Object, repo, Config, auth: null, Ct);

        Assert.False(result.Ok);
        Assert.Contains("the remote hung up", result.Error, StringComparison.Ordinal);
        Assert.Contains("could not be removed", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_killed_clone_into_an_empty_folder_that_was_already_there_leaves_nothing_and_the_next_clone_succeeds()
    {
        var repo = new Repository { Id = Guid.NewGuid(), Name = "widgets", CloneUrl = CreateRemote() };
        var target = BaseRepoPath.Fallback(repo.Id, Config);
        Directory.CreateDirectory(target);
        var real = RealGit;
        var attempts = 0;
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var git = new Mock<IRepositoryManager>();
        git.Setup(g => g.CloneAsync(repo.CloneUrl, target, It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .Returns((string url, string path, CancellationToken token, GitAuthOptions? _) =>
            {
                if (++attempts > 1)
                    return real.CloneAsync(url, path, token);
                // Killed mid-checkout: git wrote its .git and some of the files.
                Directory.CreateDirectory(Path.Combine(path, ".git", "objects"));
                File.WriteAllText(Path.Combine(path, "README.md"), "half checked out");
                shutdown.Cancel();
                return Task.FromException<(bool, string?)>(new OperationCanceledException(shutdown.Token));
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BaseRepoClone.EnsureAsync(git.Object, repo, Config, auth: null, shutdown.Token));

        Assert.False(Directory.Exists(target), "the killed clone left files behind");
        Assert.Null(BaseRepoPath.Existing(repo, Config));

        var next = await BaseRepoClone.EnsureAsync(git.Object, repo, Config, auth: null, Ct);

        Assert.True(next.Ok, next.Error);
        Assert.Equal("widgets", File.ReadAllText(Path.Combine(target, "README.md")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_or_killed_clone_leaves_a_folder_that_already_held_something_as_it_was(bool killed)
    {
        var repo = new Repository { Id = Guid.NewGuid(), Name = "widgets", CloneUrl = "https://forge.example.test/team/widgets.git" };
        var target = BaseRepoPath.Fallback(repo.Id, Config);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "NOTES.md"), "put here by hand");
        var git = new Mock<IRepositoryManager>();
        git.Setup(g => g.CloneAsync(repo.CloneUrl, target, It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()))
            .Returns(() => killed
                ? Task.FromException<(bool, string?)>(new OperationCanceledException())
                : Task.FromResult<(bool, string?)>((false, "fatal: destination path already exists and is not an empty directory.")));

        if (killed)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BaseRepoClone.EnsureAsync(git.Object, repo, Config, auth: null, Ct));
        else
            Assert.False((await BaseRepoClone.EnsureAsync(git.Object, repo, Config, auth: null, Ct)).Ok);

        Assert.Equal("put here by hand", File.ReadAllText(Path.Combine(target, "NOTES.md")));
    }

    // -- Setup -------------------------------------------------------------------------

    /// <summary>
    /// The chat's owner deletes it, through ChatService in a request of its own,
    /// which stops the clone work through the same coordinator the cloner uses.
    /// </summary>
    private async Task DeleteChatAsync(Guid chatId, string scratch)
    {
        await using var db = _db.OnOwnConnection();
        var chats = new ChatService(db, new ProviderStore(db), Mock.Of<IAgentAdapterRegistry>(), Mock.Of<IChatNotifier>(),
            new ChatOptions { ScratchRoot = _dir }, new LoopRunStore(db), new ChatLoopScratchpad(), _chatClones);

        Assert.True(await chats.DeleteAsync("scheduler", chatId, Ct).WaitAsync(Ct));
        Assert.False(Directory.Exists(scratch));
    }

    private async Task<(Guid ChatId, string Scratch, Repository Repo)> SeedScheduledChatAsync(string remote)
    {
        var provider = new RemoteProvider
        {
            Id = Guid.NewGuid(), Name = "forge", Type = "forgejo", Url = "https://forge.example.test",
            ApiKey = "the-key", CreatedAt = DateTime.UtcNow,
        };
        var repo = new Repository
        {
            Id = Guid.NewGuid(), Name = "widgets", RemoteProviderId = provider.Id, CloneUrl = remote, CreatedAt = DateTime.UtcNow,
        };
        var schedule = new ChatSchedule
        {
            Id = Guid.NewGuid(), UserId = "scheduler", Name = "nightly check", Prompt = "Check.", CronExpression = "0 3 * * *",
            TimeZone = "UTC", RepositoryScope = ChatScheduleRepositoryScope.All, CreatedAt = DateTime.UtcNow,
        };
        var chat = new ChatSession
        {
            Id = Guid.NewGuid(), UserId = "scheduler", AiProviderId = Guid.NewGuid(), ProviderType = "claude-code",
            ToolAllowlistCsv = "ild", ScratchPath = Path.Combine(_dir, "scratch"), ScheduleId = schedule.Id, CreatedAt = DateTime.UtcNow,
        };
        Directory.CreateDirectory(chat.ScratchPath);
        _db.Context.AddRange(provider, repo, schedule, chat);
        await _db.Context.SaveChangesAsync(Ct);
        return (chat.Id, chat.ScratchPath, repo);
    }

    /// <summary>A local bare repository with <c>main</c> and <c>feature</c>.</summary>
    private string CreateRemote()
    {
        var bare = Path.Combine(_dir, "remote.git");
        var work = Path.Combine(_dir, "remote-work");
        Git(_dir, "init", "-q", "--bare", "-b", "main", bare);
        Git(_dir, "clone", "-q", bare, work);
        File.WriteAllText(Path.Combine(work, "README.md"), "widgets");
        Git(work, "add", "README.md");
        Git(work, "commit", "-q", "-m", "first");
        Git(work, "push", "-q", "origin", "main");
        Git(work, "checkout", "-q", "-b", "feature");
        File.WriteAllText(Path.Combine(work, "README.md"), "widgets on feature");
        Git(work, "commit", "-q", "-am", "feature");
        Git(work, "push", "-q", "origin", "feature");
        return bare;
    }

    private void PushCommit(string branch, string file)
    {
        var work = Path.Combine(_dir, "remote-work");
        Git(work, "checkout", "-q", branch);
        File.WriteAllText(Path.Combine(work, file), file);
        Git(work, "add", file);
        Git(work, "commit", "-q", "-m", file);
        Git(work, "push", "-q", "origin", branch);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.test", "-c", "commit.gpgsign=false" })
            psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr.Result}");
        return stdout.Trim();
    }
}
