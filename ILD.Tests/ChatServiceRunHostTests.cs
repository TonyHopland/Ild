using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Container;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Core.Services.RunHosts.Local;
using ILD.Data.Entities;
using ILD.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>A chat's folder is made and removed through the run host, which knows where chats are kept.</summary>
public sealed class ChatServiceRunHostTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeRunHost _host = new();

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ChatService NewService()
    {
        var registry = Mock.Of<IAgentAdapterRegistry>(r =>
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => Mock.Of<IAgentAdapter>())).WithDeclaredTraits();
        return new ChatService(
            _db.Context, _db.Providers, registry, Mock.Of<IChatNotifier>(),
            new ChatOptions { ScratchRoot = "/data/chat-sessions" }, _db.LoopRuns, new ChatLoopScratchpad(),
            new ChatCloneCoordinator(), claudeProjects: TestClaudeProjectFolders.Instance, runHost: _host);
    }

    private async Task<Guid> SeedProviderAsync()
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(), Name = "pi", Type = "pi", BaseUrl = "http://localhost", Model = "m", Parallelism = 1, CreatedAt = DateTime.UtcNow,
        };
        _db.Context.AiProviders.Add(provider);
        await _db.Context.SaveChangesAsync(Ct);
        return provider.Id;
    }

    [Fact]
    public async Task A_new_chat_works_in_the_folder_the_run_host_made_for_it()
    {
        _host.ChatFolderFor = (id, _) => RunPaths.ChatRoot(id);

        var started = await NewService().StartAsync("alice", await SeedProviderAsync(), null, Ct);

        var stored = await _db.Context.ChatSessions.AsNoTracking().SingleAsync(c => c.Id == started.Id, Ct);
        Assert.Equal([started.Id], _host.ChatFolders);
        Assert.Equal(RunPaths.ChatRoot(started.Id), stored.ScratchPath);
    }

    [Fact]
    public async Task A_turn_on_a_chat_whose_folder_is_on_ilds_disk_is_refused_and_runs_nothing()
    {
        var local = new FakeRunHost();
        using var container = new ContainerRunHost(
            ContainerRunHostOptions.From(name => name switch
            {
                "ILD_API_URL" => "http://ild.example:8080",
                "ILD_CONTAINER_SOCKET" => "/nonexistent.sock",
                "ILD_CONTAINER_RUNNER_IMAGE" => "registry.example/ild-runner:1",
                _ => null,
            }), new ProcessRunner(), NullLogger<ContainerRunHost>.Instance);
        var adapter = new Mock<IAgentAdapter>(MockBehavior.Strict);
        var registry = Mock.Of<IAgentAdapterRegistry>(r =>
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter.Object)).WithDeclaredTraits();
        var service = new ChatService(
            _db.Context, _db.Providers, registry, Mock.Of<IChatNotifier>(),
            new ChatOptions { ScratchRoot = "/data/chat-sessions" }, _db.LoopRuns, new ChatLoopScratchpad(),
            new ChatCloneCoordinator(), claudeProjects: TestClaudeProjectFolders.Instance, runHost: new RoutingRunHost(local, container));
        var chat = new ChatSession
        {
            Id = Guid.NewGuid(), UserId = "alice", AiProviderId = await SeedProviderAsync(), ScratchPath = "/data/chat-sessions/old", CreatedAt = DateTime.UtcNow,
        };
        _db.Context.ChatSessions.Add(chat);
        await _db.Context.SaveChangesAsync(Ct);

        await service.ExecuteTurnAsync(chat.Id, Guid.NewGuid(), "hello", Ct);

        var last = await _db.Context.ChatMessages.AsNoTracking().Where(m => m.ChatSessionId == chat.Id).OrderBy(m => m.Sequence).LastAsync(Ct);
        Assert.Contains("before ILD ran everything in containers", last.Content);
        Assert.Empty(local.Started);
    }

    [Fact]
    public async Task Deleting_a_chat_deletes_its_folder_through_the_run_host()
    {
        _host.ChatFolderFor = (id, _) => RunPaths.ChatRoot(id);
        var service = NewService();
        var started = await service.StartAsync("alice", await SeedProviderAsync(), null, Ct);

        Assert.True(await service.DeleteAsync("alice", started.Id, Ct));

        Assert.False(await _host.DirectoryExistsAsync(RunPaths.ChatRoot(started.Id), Ct));
        Assert.Empty(await _db.Context.ChatSessions.AsNoTracking().Where(c => c.Id == started.Id).ToListAsync(Ct));
    }

    [Fact]
    public async Task A_chat_folder_that_cannot_be_deleted_because_the_container_is_unreachable_does_not_fail_the_delete()
    {
        var service = NewService();
        var started = await service.StartAsync("alice", await SeedProviderAsync(), null, Ct);
        var broken = new Mock<ILD.Core.Services.RunHosts.IRunHost>();
        broken.Setup(h => h.DeleteAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("could not run a command in the container"));
        var failing = new ChatService(
            _db.Context, _db.Providers, Mock.Of<IAgentAdapterRegistry>(), Mock.Of<IChatNotifier>(),
            new ChatOptions { ScratchRoot = "/data/chat-sessions" }, _db.LoopRuns, new ChatLoopScratchpad(),
            new ChatCloneCoordinator(), claudeProjects: TestClaudeProjectFolders.Instance, runHost: broken.Object);

        Assert.True(await failing.DeleteAsync("alice", started.Id, Ct));
    }
}
