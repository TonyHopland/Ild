using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

/// <summary>
/// A chat that lives in the chat container cannot see ILD's disk, so its clone is filled from ILD's own base clone with a
/// git bundle, and its git commands run in the clone, which is what sends them to the container. The container is a
/// <see cref="FakeRunHost"/> here that answers git as a clone being made would; the base clone and the remote are real.
/// </summary>
[Collection(GitBundleScratchCollection.Name)]
public sealed class ChatContainerCloneTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ild-chat-container-clone-").FullName;
    private readonly TestDb _db = new();
    private readonly ServiceProvider _services;
    private readonly FakeRunHost _host = new();
    private readonly string _remote;
    private readonly Guid _chat = Guid.NewGuid();
    private string _head = "0123456789abcdef0123456789abcdef01234567";

    public ChatContainerCloneTests()
    {
        _services = new ServiceCollection().AddScoped(_ => _db.OnOwnConnection()).BuildServiceProvider();
        _remote = BuildRemote(_dir);
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Scratch => RunPaths.ChatRoot(_chat);

    private IConfiguration Config => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:DataPath"] = Path.Combine(_dir, "data") })
        .Build();

    private ChatRepositoryCloner Cloner()
        => new(_db.Context, _services.GetRequiredService<IServiceScopeFactory>(), new ChatCloneCoordinator(), _db.Providers,
            new RepositoryManager(worktreesRoot: Path.Combine(_dir, "worktrees")), Config, ShutdownState.NeverStopping, host: _host);

    private static IReadOnlyList<string> GitArguments(RunCommand command)
    {
        var arguments = command.Start.ArgumentList.ToList();
        return arguments[(arguments.IndexOf("-C") + 2)..];
    }

    // What git prints in a clone that is made now: no commit yet, no local branch, the head the test says.
    private void AnswerGit(Func<IReadOnlyList<string>, FakeCommandScript?>? special = null)
        => _host.Script = command =>
        {
            var args = GitArguments(command);
            if (special?.Invoke(args) is { } answer) return answer;
            return args switch
            {
                ["rev-parse", "--verify", "-q", "HEAD^{commit}"] => FakeCommandScript.Exits(1),
                ["rev-parse", "--verify", "-q", _] => FakeCommandScript.Exits(1),
                ["rev-parse", "HEAD"] => FakeCommandScript.Prints(_head + "\n"),
                _ => FakeCommandScript.Exits(0),
            };
        };

    private async Task<(Repository Repo, string Folder)> SeedAsync()
    {
        var provider = new RemoteProvider
        {
            Id = Guid.NewGuid(), Name = "forge", Type = "forgejo", Url = "https://forge.example.test",
            ApiKey = "the-key", CreatedAt = DateTime.UtcNow,
        };
        var repo = new Repository
        {
            Id = Guid.NewGuid(), Name = "widgets", RemoteProviderId = provider.Id, CloneUrl = _remote, CreatedAt = DateTime.UtcNow,
        };
        var schedule = new ChatSchedule
        {
            Id = Guid.NewGuid(), UserId = "scheduler", Name = "nightly check", Prompt = "Check.", CronExpression = "0 3 * * *",
            TimeZone = "UTC", RepositoryScope = ChatScheduleRepositoryScope.All, CreatedAt = DateTime.UtcNow,
        };
        var chat = new ChatSession
        {
            Id = _chat, UserId = "scheduler", AiProviderId = Guid.NewGuid(), ProviderType = "claude-code",
            ToolAllowlistCsv = "ild", ScratchPath = Scratch, ScheduleId = schedule.Id, CreatedAt = DateTime.UtcNow,
        };
        _db.Context.AddRange(provider, repo, schedule, chat);
        await _db.Context.SaveChangesAsync(Ct);
        _host.WithDirectory(Scratch);
        return (repo, $"{Scratch}/repos/widgets");
    }

    [Fact]
    public async Task The_branch_goes_into_the_chat_container_as_a_bundle_and_every_git_command_runs_in_the_clone_there()
    {
        var (repo, folder) = await SeedAsync();
        AnswerGit();

        var result = await Cloner().CloneAsync(_chat, repo.Id.ToString(), branch: null, Ct);

        Assert.Equal(ChatRepositoryCloneOutcome.Cloned, result.Outcome);
        Assert.Equal("repos/widgets", result.Path);
        Assert.Equal(_head, result.Commit);
        Assert.Equal(["widgets"], await _host.ListDirectoriesAsync($"{Scratch}/repos", Ct));

        Assert.All(_host.Started, c =>
        {
            Assert.Equal(folder, c.Start.WorkingDirectory);
            Assert.Equal(_chat, c.OwnerId);
            Assert.DoesNotContain(c.Start.ArgumentList, a => a.StartsWith(Path.Combine(_dir, "data"), StringComparison.Ordinal));
        });
        var (bundlePath, bundle) = Assert.Single(_host.WrittenStreams);
        Assert.Matches($"^{Regex.Escape(Scratch)}/\\.ild/sync-[0-9a-f]{{32}}\\.bundle$", bundlePath);
        Assert.StartsWith("# v2 git bundle", Encoding.ASCII.GetString(bundle, 0, 15));
        Assert.Contains("refs/remotes/origin/main", Encoding.ASCII.GetString(bundle));
        Assert.False(await _host.FileExistsAsync(bundlePath, Ct), "the bundle stays in the chat's folder");
        var fetch = Assert.Single(_host.Started, c => GitArguments(c) is ["fetch", ..]);
        Assert.Equal("+refs/remotes/origin/main:refs/remotes/origin/main", GitArguments(fetch)[^1]);
        Assert.Equal(bundlePath, GitArguments(fetch)[^2]);
        Assert.Contains(_host.Started, c => GitArguments(c) is ["init", "-q"]);
    }

    [Fact]
    public async Task The_toolchains_of_the_cloned_repository_are_asked_for_the_chat_from_ilds_base_clone()
    {
        var (repo, _) = await SeedAsync();
        AnswerGit();

        await Cloner().CloneAsync(_chat, repo.Id.ToString(), branch: null, Ct);

        var (chatPath, basePath) = Assert.Single(_host.ChatTools);
        Assert.Equal(Scratch, chatPath);
        Assert.True(Directory.Exists(Path.Combine(basePath, ".git")) || File.Exists(Path.Combine(basePath, "HEAD")));
    }

    [Fact]
    public async Task A_runs_bundle_of_the_base_clone_carries_nothing_of_a_chat_clone_made_from_it()
    {
        var (repo, _) = await SeedAsync();
        AnswerGit();

        await Cloner().CloneAsync(_chat, repo.Id.ToString(), branch: null, Ct);

        var basePath = BaseRepoPath.Existing(repo, Config)!;
        var bundle = Path.Combine(_dir, "run.bundle");
        GitOutput(basePath, "bundle", "create", "--quiet", bundle, "--all");
        Assert.DoesNotContain("ild-chat", GitOutput(basePath, "bundle", "list-heads", bundle));
        Assert.DoesNotContain("ild-chat", GitOutput(basePath, "for-each-ref"));
    }

    [Fact]
    public async Task A_clone_already_in_the_container_is_found_by_its_marker_and_updated_not_made_again()
    {
        var (repo, folder) = await SeedAsync();
        _host.WithDirectory($"{Scratch}/repos").WithDirectory(folder);
        AnswerGit(args => args switch
        {
            ["config", "--local", "--get", "ild.repositoryId"] => FakeCommandScript.Prints(repo.Id + "\n"),
            ["rev-parse", "--git-dir", "--git-common-dir"] => FakeCommandScript.Prints(".git\n.git\n"),
            ["rev-parse", "--verify", "-q", "HEAD^{commit}"] => FakeCommandScript.Prints("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n"),
            ["symbolic-ref", "-q", "--short", "HEAD"] => FakeCommandScript.Prints("main\n"),
            ["rev-parse", "--verify", "-q", "refs/heads/main"] => FakeCommandScript.Prints(_head + "\n"),
            _ => null,
        });

        var result = await Cloner().CloneAsync(_chat, repo.Id.ToString(), branch: null, Ct);

        Assert.Equal(ChatRepositoryCloneOutcome.Updated, result.Outcome);
        Assert.DoesNotContain(_host.Started, c => GitArguments(c) is ["init", ..]);
        Assert.Contains(_host.Started, c => GitArguments(c) is ["merge", "-q", "--ff-only", "refs/remotes/origin/main"]);
        Assert.Single(_host.WrittenStreams);
    }

    [Fact]
    public async Task A_folder_that_is_not_the_repositorys_clone_is_refused_and_nothing_is_fetched()
    {
        var (repo, folder) = await SeedAsync();
        _host.WithDirectory($"{Scratch}/repos").WithDirectory(folder).WithDirectory($"{folder}-{repo.Id:N}");
        AnswerGit(args => args switch
        {
            ["config", "--local", "--get", "ild.repositoryId"] => FakeCommandScript.Exits(1),
            _ => null,
        });

        var result = await Cloner().CloneAsync(_chat, repo.Id.ToString(), branch: null, Ct);

        Assert.Equal(ChatRepositoryCloneOutcome.CloneFailed, result.Outcome);
        Assert.Empty(_host.WrittenStreams);
    }

    [Fact]
    public async Task A_bundle_git_in_the_container_cannot_fetch_fails_the_clone_with_what_it_said_and_leaves_none_behind()
    {
        var (repo, _) = await SeedAsync();
        AnswerGit(args => args is ["fetch", ..] ? FakeCommandScript.Prints("", "fatal: the bundle is corrupt", 128) : null);

        var result = await Cloner().CloneAsync(_chat, repo.Id.ToString(), branch: null, Ct);

        Assert.Equal(ChatRepositoryCloneOutcome.CloneFailed, result.Outcome);
        Assert.Contains("the bundle is corrupt", result.Message);
        Assert.False(await _host.FileExistsAsync(Assert.Single(_host.WrittenStreams).Path, Ct));
    }

    private static string GitOutput(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
        return output.Trim();
    }

    private static string BuildRemote(string root)
    {
        var bare = Path.Combine(root, "remote.git");
        var work = Path.Combine(root, "remote-work");
        GitOutput(root, "init", "-q", "--bare", "-b", "main", bare);
        GitOutput(root, "clone", "-q", bare, work);
        File.WriteAllText(Path.Combine(work, "README.md"), "widgets");
        GitOutput(work, "add", "README.md");
        GitOutput(work, "-c", "user.email=t@example.test", "-c", "user.name=T", "commit", "-q", "-m", "first");
        GitOutput(work, "push", "-q", "origin", "main");
        return bare;
    }
}
