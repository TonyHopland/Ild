using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The git commands ILD runs in a chat's clone run as the agent, from outside
/// the clone, and inherit none of the orchestrator's credentials. The
/// orchestrator's own environment is seeded with them here, because what this
/// proves is that a command does not inherit them.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public sealed class ChatRepositoryClonerStartInfoTests
{
    private static readonly string[] Inherited =
    [
        "GIT_ASKPASS", "ILD_GIT_PASSWORD", "ILD_GIT_USERNAME", "GIT_DIR", "GIT_WORK_TREE",
        "ILD_DB_CONNECTION_STRING", "ILD_SECRET_KEY", "ILD_API_TOKEN", "ILD_AGENT_TOKEN",
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Git_in_the_clone_runs_as_the_agent_from_outside_it_with_no_credentials_in_its_environment(bool isolation)
    {
        var saved = Inherited.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in Inherited)
                Environment.SetEnvironmentVariable(name, "orchestrator-only-" + name);

            var psi = ChatRepositoryCloner.BuildStartInfo(
                ["-C", "/scratch/chat/repos/widgets", "fetch", "--no-tags", "/data/repos/base"],
                isolation ? "agent" : null, isolation ? "agent" : null, isolation ? "/home/agent" : null);

            if (isolation)
            {
                Assert.Equal("/usr/bin/setpriv", psi.FileName);
                Assert.Contains(psi.ArgumentList, a => Path.GetFileName(a) == "git");
            }
            else
            {
                Assert.Equal("git", Path.GetFileName(psi.FileName));
            }
            Assert.Contains("/scratch/chat/repos/widgets", psi.ArgumentList);
            Assert.Equal("/", psi.WorkingDirectory);
            foreach (var name in Inherited)
                Assert.False(psi.Environment.ContainsKey(name), $"{name} reaches git in the agent's clone");
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}

/// <summary>
/// ILD's base clone of a repository, cloned or fetched on a chat's behalf. A
/// fresh clone that fails or is killed must leave nothing behind: the base clone
/// is found by its .git folder, so a partial one would be fetched forever after
/// instead of being cloned again, by chats and by runs' Start nodes alike.
/// </summary>
public sealed class BaseRepoCloneTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ild-base-clone-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private IConfiguration Config => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:DataPath"] = _dir })
        .Build();

    private static Repository Repo(string? worktreesPath = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "widgets",
        CloneUrl = "https://forge.example.test/team/widgets.git",
        WorktreesPath = worktreesPath,
    };

    private static readonly GitAuthOptions Auth = new("https://forge.example.test/team/widgets.git", "the-key", "forgejo");

    /// <summary>A clone that gets as far as writing part of a .git folder.</summary>
    private static void WritePartialClone(string target)
    {
        Directory.CreateDirectory(Path.Combine(target, ".git", "objects"));
        File.WriteAllText(Path.Combine(target, ".git", "HEAD"), "ref: refs/heads/main\n");
    }

    [Fact]
    public async Task A_fresh_clone_that_fails_leaves_no_folder_behind()
    {
        var repo = Repo();
        var target = BaseRepoPath.Fallback(repo.Id, Config);
        var git = new Mock<IRepositoryManager>();
        git.Setup(g => g.CloneAsync(repo.CloneUrl, target, It.IsAny<CancellationToken>(), Auth))
            .Returns((string _, string path, CancellationToken _, GitAuthOptions? _) =>
            {
                WritePartialClone(path);
                return Task.FromResult<(bool, string?)>((false, "fatal: the remote hung up unexpectedly"));
            });

        var result = await BaseRepoClone.EnsureAsync(git.Object, repo, Config, Auth, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.False(Directory.Exists(target));
        Assert.Null(BaseRepoPath.Existing(repo, Config));
    }

    [Fact]
    public async Task A_fresh_clone_that_is_killed_leaves_no_folder_behind()
    {
        var repo = Repo();
        var target = BaseRepoPath.Fallback(repo.Id, Config);
        using var shutdown = new CancellationTokenSource();
        var git = new Mock<IRepositoryManager>();
        git.Setup(g => g.CloneAsync(repo.CloneUrl, target, It.IsAny<CancellationToken>(), Auth))
            .Returns((string _, string path, CancellationToken _, GitAuthOptions? _) =>
            {
                WritePartialClone(path);
                shutdown.Cancel();
                return Task.FromException<(bool, string?)>(new OperationCanceledException(shutdown.Token));
            });

        try
        {
            var result = await BaseRepoClone.EnsureAsync(git.Object, repo, Config, Auth, shutdown.Token);
            Assert.False(result.Ok);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.False(Directory.Exists(target));
        Assert.Null(BaseRepoPath.Existing(repo, Config));
    }

    [Fact]
    public async Task An_existing_base_clone_is_fetched_where_it_is_and_kept_when_the_fetch_fails()
    {
        var existing = Path.Combine(_dir, "configured-worktrees");
        WritePartialClone(existing);
        File.WriteAllText(Path.Combine(existing, "README.md"), "checked out");
        var repo = Repo(worktreesPath: existing);
        var git = new Mock<IRepositoryManager>();
        git.Setup(g => g.FetchAsync(existing, It.IsAny<CancellationToken>(), Auth)).ReturnsAsync(false);

        var failed = await BaseRepoClone.EnsureAsync(git.Object, repo, Config, Auth, TestContext.Current.CancellationToken);

        Assert.False(failed.Ok);
        Assert.False(string.IsNullOrWhiteSpace(failed.Error));
        Assert.True(File.Exists(Path.Combine(existing, "README.md")));
        git.Verify(g => g.CloneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<GitAuthOptions?>()), Times.Never);

        git.Setup(g => g.FetchAsync(existing, It.IsAny<CancellationToken>(), Auth)).ReturnsAsync(true);

        var fetched = await BaseRepoClone.EnsureAsync(git.Object, repo, Config, Auth, TestContext.Current.CancellationToken);

        Assert.True(fetched.Ok);
        Assert.Equal(existing, fetched.Path);
    }
}

/// <summary>clone_repository as the agent sees it: its name and parameters, and the agent-API call it makes.</summary>
public sealed class CloneRepositoryToolTests
{
    private static Task<(string Result, HttpRequestMessage Request, string? Body)> InvokeAsync(IReadOnlyDictionary<string, object?> args)
        => McpToolInvoker.InvokeAsync("clone_repository", args,
            """{"outcome":"Cloned","path":"repos/widgets","branch":"main","commit":"abc123","previousCommit":null,"changed":true}""");

    [Fact]
    public async Task clone_repository_posts_the_branch_to_the_repositorys_clone_endpoint()
    {
        var (result, request, body) = await InvokeAsync(new Dictionary<string, object?>
        {
            ["repositoryId"] = "3f2c a/b",
            ["branch"] = "release/1.2",
        });

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"{McpToolInvoker.BaseAddress}api/v1/agent/chat/repositories/3f2c%20a%2Fb/clone", request.RequestUri!.AbsoluteUri);
        Assert.Equal("release/1.2", JsonDocument.Parse(body!).RootElement.GetProperty("branch").GetString());
        Assert.Contains("repos/widgets", result);
    }

    [Fact]
    public async Task clone_repository_without_a_branch_leaves_the_choice_to_ILD()
    {
        var (_, request, body) = await InvokeAsync(new Dictionary<string, object?> { ["repositoryId"] = "3f2c" });

        Assert.Equal($"{McpToolInvoker.BaseAddress}api/v1/agent/chat/repositories/3f2c/clone", request.RequestUri!.AbsoluteUri);
        var json = JsonDocument.Parse(body!).RootElement;
        Assert.True(!json.TryGetProperty("branch", out var branch) || branch.ValueKind == JsonValueKind.Null);
    }
}
