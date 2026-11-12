using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

public sealed class ChatPreparationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeRunHost _host = new();
    private readonly Mock<IChatNotifier> _notifier = new();
    private readonly ChatPreparations _preparations;
    private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-prep-tests", Guid.NewGuid().ToString("N"));
    private int _agentRuns;

    public ChatPreparationTests() => _preparations = new ChatPreparations(_notifier.Object);

    public void Dispose() => _db.Dispose();

    private sealed class CountingAdapter(Action onRun) : IAgentAdapter
    {
        public string Name => "fake";
        public string[] SupportedProviderTypes => ["fake"];
        public ConfigFieldDescriptor[] ConfigSchema => [];
        public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;
        public AgentAdapterTraits Traits => AgentAdapterTraits.None;

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            onRun();
            return Task.FromResult(NodeExecutionResult.Ok("done"));
        }
    }

    private ChatService NewService()
    {
        var adapter = new CountingAdapter(() => Interlocked.Increment(ref _agentRuns));
        var registry = Mock.Of<IAgentAdapterRegistry>(r =>
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter)).WithDeclaredTraits();
        return new ChatService(_db.Context, _db.Providers, registry, _notifier.Object, new ChatOptions { ScratchRoot = _scratchRoot },
            _db.LoopRuns, new ChatLoopScratchpad(), new ChatCloneCoordinator(),
            claudeProjects: TestClaudeProjectFolders.Instance, runHost: _host, preparations: _preparations);
    }

    private async Task<AiProvider> SeedProviderAsync()
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(), Name = "p1", Type = "claude-code", BaseUrl = "http://localhost", Model = "m",
            Parallelism = 1, CreatedAt = DateTime.UtcNow,
        };
        _db.Context.AiProviders.Add(provider);
        await _db.Context.SaveChangesAsync();
        return provider;
    }

    private async Task<List<ChatMessage>> MessagesAsync(Guid chatId)
    {
        using var ctx = _db.Fresh();
        return await ctx.ChatMessages.AsNoTracking().Where(m => m.ChatSessionId == chatId).OrderBy(m => m.Sequence).ToListAsync();
    }

    [Fact]
    public async Task StartAsync_returns_while_the_container_is_still_being_prepared()
    {
        var gate = new TaskCompletionSource();
        _host.ChatFolderGate = gate.Task;
        var provider = await SeedProviderAsync();
        var svc = NewService();

        var view = await svc.StartAsync("alice", provider.Id, null, TestContext.Current.CancellationToken);

        Assert.True(_preparations.IsPreparing(view.Id));
        Assert.Empty(_host.ChatFolders);
        gate.SetResult();
        await _preparations.WaitAsync(view.Id, TestContext.Current.CancellationToken);
        Assert.False(_preparations.IsPreparing(view.Id));
        Assert.Contains(view.Id, _host.ChatFolders);
        _notifier.Verify(n => n.ChatPreparedAsync(view.Id), Times.Once);
    }

    [Fact]
    public async Task A_message_sent_while_preparing_runs_once_after_the_container_is_ready()
    {
        var gate = new TaskCompletionSource();
        _host.ChatFolderGate = gate.Task;
        var provider = await SeedProviderAsync();
        var svc = NewService();
        var view = await svc.StartAsync("alice", provider.Id, null, TestContext.Current.CancellationToken);

        var turn = svc.ExecuteTurnAsync(view.Id, Guid.NewGuid(), "hello", CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.False(turn.IsCompleted);
        Assert.Equal(0, _agentRuns);
        Assert.Equal(["user"], (await MessagesAsync(view.Id)).Select(m => m.Role));

        gate.SetResult();
        await turn;

        Assert.Equal(1, _agentRuns);
        Assert.Equal(["user", "assistant"], (await MessagesAsync(view.Id)).Select(m => m.Role));
    }

    [Fact]
    public async Task A_failed_preparation_replies_with_the_error_and_the_next_message_retries()
    {
        _host.ChatFolderFailure = new IOException("no room");
        var provider = await SeedProviderAsync();
        var svc = NewService();
        var view = await svc.StartAsync("alice", provider.Id, null, TestContext.Current.CancellationToken);
        Assert.Equal("no room", _preparations.FailureOf(view.Id));
        _notifier.Verify(n => n.ChatPreparationFailedAsync(view.Id, "no room"), Times.Once);

        await svc.ExecuteTurnAsync(view.Id, Guid.NewGuid(), "first", CancellationToken.None);

        var failed = await MessagesAsync(view.Id);
        Assert.Equal("first", failed[0].Content);
        Assert.StartsWith("[chat-error] The chat's container could not be prepared: no room", failed[1].Content);
        Assert.Equal(0, _agentRuns);

        _host.ChatFolderFailure = null;
        await svc.ExecuteTurnAsync(view.Id, Guid.NewGuid(), "again", CancellationToken.None);

        Assert.Equal(1, _agentRuns);
        Assert.Null(_preparations.FailureOf(view.Id));
        Assert.Contains(view.Id, _host.ChatFolders);
    }

    [Fact]
    public async Task Cancelling_a_waiting_turn_leaves_the_preparation_running()
    {
        var gate = new TaskCompletionSource();
        _host.ChatFolderGate = gate.Task;
        var provider = await SeedProviderAsync();
        var svc = NewService();
        var view = await svc.StartAsync("alice", provider.Id, null, TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();

        var turn = svc.ExecuteTurnAsync(view.Id, Guid.NewGuid(), "hello", cts.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await turn;

        Assert.True(_preparations.IsPreparing(view.Id));
        Assert.Equal(0, _agentRuns);
        Assert.True((await MessagesAsync(view.Id)).Last().Interrupted);
        gate.SetResult();
    }

    [Fact]
    public async Task Deleting_a_chat_waits_for_its_preparation()
    {
        var gate = new TaskCompletionSource();
        _host.ChatFolderGate = gate.Task;
        var provider = await SeedProviderAsync();
        var svc = NewService();
        var view = await svc.StartAsync("alice", provider.Id, null, TestContext.Current.CancellationToken);

        var delete = svc.DeleteAsync("alice", view.Id, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(delete.IsCompleted);

        gate.SetResult();
        Assert.True(await delete);
        Assert.False(_preparations.IsPreparing(view.Id));
    }
}
