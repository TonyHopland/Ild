
using ILD.WorkItemServer;
using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Dtos;
using ILD.WorkItemServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests.WorkItemServer;

public class WorkItemServiceTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private WorkItemServerDbContext _db = null!;
    private TestClock _clock = null!;
    private WorkItemService _svc = null!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkItemServerDbContext>()
            .UseSqlite(_conn)
            .Options;
        _db = new WorkItemServerDbContext(options);
        await _db.Database.EnsureCreatedAsync();
        _clock = new TestClock(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        _svc = new WorkItemService(_db, _clock);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTime Now;
        public TestClock(DateTime now) => Now = now;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    [Fact]
    public async Task Create_defaults_to_Backlog_and_persists_tags_and_dependencies()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" });
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            Description = "d",
            Priority = WorkItemPriority.High,
            Tags = new[] { "bug-fix" },
            Dependencies = new[] { dep.Id },
        });

        Assert.Equal(WorkItemStatus.Backlog, dto.Status);
        Assert.Equal("bug-fix", Assert.Single(dto.Tags));
        Assert.Equal(dep.Id, Assert.Single(dto.Dependencies));
        Assert.Equal(WorkItemPriority.High, dto.Priority);
    }

    [Fact]
    public async Task Create_honours_forceStatus_when_provided()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "x",
            ForceStatus = WorkItemStatus.Ready,
        });

        Assert.Equal(WorkItemStatus.Ready, dto.Status);
    }

    [Fact]
    public async Task Transition_to_Running_succeeds_when_no_dependencies()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });

        var resp = await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running });

        Assert.True(resp.Success);
        Assert.Equal(WorkItemStatus.Running, resp.ActualStatus);
    }

    [Fact]
    public async Task Transition_to_Running_fails_when_already_Running()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });
        await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running });

        var second = await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running });

        Assert.False(second.Success);
        Assert.Equal(WorkItemStatus.Running, second.ActualStatus);
        Assert.Equal("Already claimed", second.Reason);
    }

    [Fact]
    public async Task Transition_to_Running_fails_when_dependency_not_done()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" });
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            Dependencies = new[] { dep.Id },
        });

        var resp = await _svc.TransitionAsync(child.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running });

        Assert.False(resp.Success);
        Assert.Equal("Dependencies not satisfied", resp.Reason);
    }

    [Fact]
    public async Task Transition_to_Running_succeeds_after_dependency_done()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" });
        await _svc.TransitionAsync(dep.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done });
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            Dependencies = new[] { dep.Id },
        });

        var resp = await _svc.TransitionAsync(child.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running });

        Assert.True(resp.Success);
    }

    [Fact]
    public async Task Transition_to_HumanFeedback_appends_AI_conversation_entry_with_reason()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });

        await _svc.TransitionAsync(dto.Id, new TransitionRequest
        {
            TargetStatus = WorkItemStatus.HumanFeedback,
            Reason = "Need approval",
            Actions = "[\"approve\",\"reject\"]",
        });

        var fresh = await _svc.GetAsync(dto.Id);
        Assert.Equal(WorkItemStatus.HumanFeedback, fresh!.Status);
        Assert.Single(fresh.Conversation);
        Assert.Equal("ai", fresh.Conversation[0].Role);
        Assert.Equal("Need approval", fresh.Conversation[0].Content);
        Assert.Equal("[\"approve\",\"reject\"]", fresh.HumanFeedbackActions);
    }

    [Fact]
    public async Task Transition_to_non_response_state_does_not_append_conversation()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });

        await _svc.TransitionAsync(dto.Id, new TransitionRequest
        {
            TargetStatus = WorkItemStatus.Ready,
            Reason = "ignored",
        });

        var fresh = await _svc.GetAsync(dto.Id);
        Assert.Empty(fresh!.Conversation);
    }

    [Fact]
    public async Task Feedback_appends_human_message_and_moves_to_WaitingForIld()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });
        await _svc.TransitionAsync(dto.Id, new TransitionRequest
        {
            TargetStatus = WorkItemStatus.HumanFeedback,
            Reason = "Need approval",
        });

        await _svc.AppendFeedbackAsync(dto.Id, "approve please");

        var fresh = await _svc.GetAsync(dto.Id);
        Assert.Equal(WorkItemStatus.WaitingForIld, fresh!.Status);
        Assert.Equal(2, fresh.Conversation.Count());
        Assert.Equal("human", fresh.Conversation[1].Role);
        Assert.Equal("approve please", fresh.Conversation[1].Content);
    }

    [Fact]
    public async Task Poll_returns_active_items_and_ready_items_and_refreshes_heartbeat()
    {
        var ready = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "ready",
            ForceStatus = WorkItemStatus.Ready,
        });
        var running = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "running" });
        await _svc.TransitionAsync(running.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running });

        // advance time to verify heartbeat is updated
        _clock.Now = _clock.Now.AddMinutes(5);
        var resp = await _svc.PollAsync(new[] { running.Id });

        Assert.Equal(running.Id, Assert.Single(resp.ActiveItems).Id);
        Assert.Equal(ready.Id, Assert.Single(resp.ReadyItems).Id);

        var raw = await _db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == running.Id);
        Assert.Equal(_clock.Now, raw.LastHeartbeatAt);
    }

    [Fact]
    public async Task ReclaimStale_moves_unheartbeated_running_items_back_to_Ready()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });
        await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running });

        // advance time past timeout
        _clock.Now = _clock.Now.AddMinutes(20);
        var n = await _svc.ReclaimStaleAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(1, n);
        var fresh = await _svc.GetAsync(dto.Id);
        Assert.Equal(WorkItemStatus.Ready, fresh!.Status);
    }

    [Fact]
    public async Task ReclaimStale_does_not_touch_recently_heartbeated_items()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });
        await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running });

        _clock.Now = _clock.Now.AddMinutes(5);
        await _svc.PollAsync(new[] { dto.Id });

        _clock.Now = _clock.Now.AddMinutes(5);
        var n = await _svc.ReclaimStaleAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(0, n);
        var fresh = await _svc.GetAsync(dto.Id);
        Assert.Equal(WorkItemStatus.Running, fresh!.Status);
    }

    [Fact]
    public async Task ReclaimStale_never_reclaims_HumanFeedback_items_to_Ready()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });
        await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running });
        await _svc.TransitionAsync(dto.Id, new TransitionRequest
        {
            TargetStatus = WorkItemStatus.HumanFeedback,
            Reason = "Need approval",
        });

        // advance time far past timeout
        _clock.Now = _clock.Now.AddMinutes(30);
        var n = await _svc.ReclaimStaleAsync(TimeSpan.FromMinutes(15));

        Assert.Equal(0, n);
        var fresh = await _svc.GetAsync(dto.Id);
        Assert.Equal(WorkItemStatus.HumanFeedback, fresh!.Status);
    }

    [Fact]
    public async Task AddDependency_rejects_self_reference_and_unknown_targets()
    {
        var a = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" });

        Assert.False((await _svc.AddDependencyAsync(a.Id, a.Id)));
        Assert.False((await _svc.AddDependencyAsync(a.Id, Guid.NewGuid().ToString())));
    }

    [Fact]
    public async Task RemoveDependency_returns_false_when_dependency_not_present()
    {
        var a = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" });

        Assert.False((await _svc.RemoveDependencyAsync(a.Id, Guid.NewGuid().ToString())));
    }

    [Fact]
    public async Task ListAsync_filters_by_status_and_tags()
    {
        await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a", Tags = new[] { "feature" }, ForceStatus = WorkItemStatus.Ready });
        await _svc.CreateAsync(new CreateWorkItemRequest { Title = "b", Tags = new[] { "bug-fix" }, ForceStatus = WorkItemStatus.Ready });
        await _svc.CreateAsync(new CreateWorkItemRequest { Title = "c", Tags = new[] { "feature" }, ForceStatus = WorkItemStatus.Backlog });

        var list = await _svc.ListAsync(WorkItemStatus.Ready, new[] { "feature" });

        Assert.Equal("a", Assert.Single(list).Title);
    }
}
