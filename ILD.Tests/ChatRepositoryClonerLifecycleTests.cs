using System.Diagnostics;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ILD.Tests;

/// <summary>
/// What becomes of clone work whose chat goes away, and of a fresh base clone
/// whose partial folder cannot be removed.
/// </summary>
public sealed class ChatRepositoryClonerLifecycleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ild-clone-lifecycle-").FullName;
    private readonly TestDb _db = new();

    public void Dispose()
    {
        _db.Dispose();
        if (OperatingSystem.IsLinux())
        {
            foreach (var dir in Directory.EnumerateDirectories(_dir, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private IConfiguration Config => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:DataPath"] = Path.Combine(_dir, "data") })
        .Build();

    [Fact]
    public async Task Deleting_the_chat_stops_its_clone_and_the_scratch_folder_stays_gone()
    {
        var ct = TestContext.Current.CancellationToken;
        var remote = CreateRemote();
        var (chatId, scratch, repo) = await SeedScheduledChatAsync(remote);
        var real = new RepositoryManager(worktreesRoot: Path.Combine(_dir, "worktrees"));
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
        var cloner = new ChatRepositoryCloner(_db.Context, _db.Providers, git.Object, Config, ShutdownState.NeverStopping);

        var call = cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, ct);
        await entered.Task.WaitAsync(ct);

        // As ChatService deletes a chat: stop its clone work, then remove its scratch folder.
        await ChatRepositoryCloner.StopForDeletedChatAsync(chatId).WaitAsync(ct);
        Directory.Delete(scratch, recursive: true);
        released.TrySetResult();
        var result = await call;

        Assert.Equal(ChatRepositoryCloneOutcome.UnknownChat, result.Outcome);
        Assert.False(Directory.Exists(scratch), "the clone brought the deleted chat's scratch folder back");

        var later = await cloner.CloneAsync(chatId, repo.Id.ToString(), branch: null, ct);

        Assert.Equal(ChatRepositoryCloneOutcome.UnknownChat, later.Outcome);
        Assert.False(Directory.Exists(scratch));
    }

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

        var result = await BaseRepoClone.EnsureAsync(git.Object, repo, Config, auth: null, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Contains("the remote hung up", result.Error, StringComparison.Ordinal);
        Assert.Contains("could not be removed", result.Error, StringComparison.Ordinal);
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
        await _db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (chat.Id, chat.ScratchPath, repo);
    }

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
        return bare;
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.test", "-c", "commit.gpgsign=false" })
            psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr.Result}");
    }
}
