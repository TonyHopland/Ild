using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Unit coverage for the standalone chat orchestrator (ADR-0010): lifecycle,
/// the single-turn wrapper, session binding, the interrupt path, and the
/// hard-delete that leaves nothing chat-local behind.
/// </summary>
public sealed class ChatServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly RecordingChatNotifier _notifier = new();
    private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-tests", Guid.NewGuid().ToString("N"));

    private ChatOptions Options => new() { ScratchRoot = _scratchRoot };

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, true); } catch { }
    }

    /// <summary>Fake adapter that records its context and replays a scripted turn.</summary>
    private sealed class FakeAdapter : IAgentAdapter
    {
        private readonly Func<AgentExecutionContext, Task<NodeExecutionResult>> _run;
        public AgentExecutionContext? LastContext { get; private set; }

        public FakeAdapter(Func<AgentExecutionContext, Task<NodeExecutionResult>> run) => _run = run;

        public string Name => "fake";
        public string[] SupportedProviderTypes => ["fake"];
        public ConfigFieldDescriptor[] ConfigSchema => [];

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            LastContext = context;
            return _run(context);
        }
    }

    private sealed record AppendedMessage(Guid ChatSessionId, ChatMessageView Message);

    private sealed class RecordingChatNotifier : IChatNotifier
    {
        public List<AppendedMessage> Appended { get; } = new();
        public List<string> Progress { get; } = new();
        public List<bool> Completed { get; } = new();

        public Task MessageAppendedAsync(Guid chatSessionId, ChatMessageView message)
        {
            Appended.Add(new AppendedMessage(chatSessionId, message));
            return Task.CompletedTask;
        }

        public Task TurnProgressAsync(Guid chatSessionId, string delta)
        {
            Progress.Add(delta);
            return Task.CompletedTask;
        }

        public Task TurnCompletedAsync(Guid chatSessionId, bool interrupted)
        {
            Completed.Add(interrupted);
            return Task.CompletedTask;
        }
    }

    private static IAgentAdapterRegistry RegistryFor(IAgentAdapter adapter)
        => Mock.Of<IAgentAdapterRegistry>(r =>
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter));

    private async Task<AiProvider> SeedProviderAsync(string type = "claude-code")
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "p1",
            Type = type,
            BaseUrl = "http://localhost",
            Model = "m",
            Parallelism = 1,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Context.AiProviders.Add(provider);
        await _db.Context.SaveChangesAsync();
        return provider;
    }

    private ChatService NewService(IAgentAdapter adapter)
        => new(_db.Context, _db.Providers, RegistryFor(adapter), _notifier, Options);

    [Fact]
    public async Task StartAsync_creates_session_with_scratch_dir_and_normalized_tools()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok())));

        var view = await svc.StartAsync("alice", provider.Id, new[] { "ild", "read" });

        Assert.Equal(provider.Id, view.AiProviderId);
        Assert.Contains("ild", view.Tools);
        var session = _db.Context.ChatSessions.Single();
        Assert.Equal("alice", session.UserId);
        Assert.True(Directory.Exists(session.ScratchPath), "scratch directory should be created");
    }

    [Fact]
    public async Task StartAsync_rejects_a_second_session_for_the_same_user()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok())));
        await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.StartAsync("alice", provider.Id, new[] { "ild" }));
    }

    [Fact]
    public async Task ExecuteTurnAsync_appends_turn_binds_session_and_streams_progress()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(async ctx =>
        {
            ctx.OnSessionId?.Invoke("sess-1");
            await ctx.ProgressCallback!("hello ");
            return NodeExecutionResult.Ok("hello world", sessionId: "sess-1");
        });
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        await svc.ExecuteTurnAsync(started.Id, "hi there", CancellationToken.None);

        // The synthesized context routes through the chat session, not a run.
        Assert.Equal(started.Id, adapter.LastContext!.ChatSessionId);
        Assert.True(adapter.LastContext.ManageSession);

        var messages = _db.Context.ChatMessages
            .Where(m => m.ChatSessionId == started.Id)
            .OrderBy(m => m.Sequence)
            .ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("hi there", messages[0].Content);
        Assert.Equal("assistant", messages[1].Role);
        Assert.Equal("hello world", messages[1].Content);
        Assert.False(messages[1].Interrupted);

        var session = _db.Context.ChatSessions.Single();
        Assert.Equal("sess-1", session.CurrentSessionId);

        Assert.Contains("hello ", _notifier.Progress);
        Assert.Equal(2, _notifier.Appended.Count);
        Assert.Single(_notifier.Completed);
        Assert.False(_notifier.Completed[0]);
    }

    [Fact]
    public async Task ExecuteTurnAsync_resumes_the_bound_session_on_the_next_turn()
    {
        var provider = await SeedProviderAsync();
        var adapter = new FakeAdapter(ctx =>
            Task.FromResult(NodeExecutionResult.Ok("ok", sessionId: "sess-1")));
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        await svc.ExecuteTurnAsync(started.Id, "first", CancellationToken.None);
        await svc.ExecuteTurnAsync(started.Id, "second", CancellationToken.None);

        // The second turn must resume the session id captured by the first.
        Assert.Equal("sess-1", adapter.LastContext!.SessionId);
    }

    [Fact]
    public async Task ExecuteTurnAsync_keeps_partial_reply_flagged_interrupted_when_cancelled()
    {
        var provider = await SeedProviderAsync();
        using var cts = new CancellationTokenSource();
        var adapter = new FakeAdapter(async ctx =>
        {
            await ctx.ProgressCallback!("partial answer");
            cts.Cancel();
            // Adapters surface a cancelled turn as a failed result after killing
            // the process; the partial streamed text is what we keep.
            return NodeExecutionResult.Fail("interrupted");
        });
        var svc = NewService(adapter);
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        await svc.ExecuteTurnAsync(started.Id, "go", cts.Token);

        var assistant = _db.Context.ChatMessages
            .Where(m => m.ChatSessionId == started.Id && m.Role == "assistant")
            .Single();
        Assert.True(assistant.Interrupted);
        Assert.Equal("partial answer", assistant.Content);
        Assert.True(_notifier.Completed[0], "turn-completed should report interrupted");
    }

    [Fact]
    public async Task EndAsync_hard_deletes_session_messages_snapshots_and_scratch_dir()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok("x"))));
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });
        await svc.ExecuteTurnAsync(started.Id, "hi", CancellationToken.None);

        // Bind a snapshot to the chat session so we can prove the cascade.
        var snapshots = new AdapterSessionSnapshotStore(_db.Context);
        await snapshots.UpsertForChatAsync(started.Id, "fake", "sess-1", "{\"events\":[]}");
        var scratchPath = _db.Context.ChatSessions.Single().ScratchPath;
        Assert.True(Directory.Exists(scratchPath));

        var ended = await svc.EndAsync("alice");

        Assert.True(ended);
        Assert.Empty(_db.Context.ChatSessions);
        Assert.Empty(_db.Context.ChatMessages);
        Assert.Empty(_db.Context.AdapterSessionSnapshots);
        Assert.False(Directory.Exists(scratchPath), "scratch directory should be removed");
    }

    [Fact]
    public async Task SweepIdleAsync_reclaims_only_sessions_idle_before_the_cutoff()
    {
        var provider = await SeedProviderAsync();
        var svc = NewService(new FakeAdapter(_ => Task.FromResult(NodeExecutionResult.Ok())));
        var started = await svc.StartAsync("alice", provider.Id, new[] { "ild" });

        // Backdate activity well before the cutoff. Done via raw SQL because the
        // context's IHasUpdatedAt hook would otherwise reset UpdatedAt to now on save.
        var old = DateTime.UtcNow.AddDays(-30);
        await _db.Context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ChatSessions\" SET \"UpdatedAt\" = {old} WHERE \"Id\" = {started.Id}");

        var removed = await svc.SweepIdleAsync(DateTimeOffset.UtcNow.AddDays(-14));

        Assert.Equal(1, removed);
        Assert.Empty(_db.Context.ChatSessions);
    }
}
