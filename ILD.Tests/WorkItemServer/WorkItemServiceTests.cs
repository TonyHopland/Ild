using FluentAssertions;

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

        dto.Status.Should().Be(WorkItemStatus.Backlog);
        dto.Tags.Should().ContainSingle().Which.Should().Be("bug-fix");
        dto.Dependencies.Should().ContainSingle().Which.Should().Be(dep.Id);
        dto.Priority.Should().Be(WorkItemPriority.High);
    }

    [Fact]
    public async Task Create_honours_forceStatus_when_provided()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "x",
            ForceStatus = WorkItemStatus.Ready,
        });

        dto.Status.Should().Be(WorkItemStatus.Ready);
    }

    [Fact]
    public async Task Transition_to_Running_succeeds_when_no_dependencies()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });

        var resp = await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running });

        resp.Success.Should().BeTrue();
        resp.ActualStatus.Should().Be(WorkItemStatus.Running);
    }

    [Fact]
    public async Task Transition_to_Running_fails_when_already_Running()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });
        await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running });

        var second = await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running });

        second.Success.Should().BeFalse();
        second.ActualStatus.Should().Be(WorkItemStatus.Running);
        second.Reason.Should().Be("Already claimed");
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

        resp.Success.Should().BeFalse();
        resp.Reason.Should().Be("Dependencies not satisfied");
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

        resp.Success.Should().BeTrue();
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
        fresh!.Status.Should().Be(WorkItemStatus.HumanFeedback);
        fresh.Conversation.Should().ContainSingle();
        fresh.Conversation[0].Role.Should().Be("ai");
        fresh.Conversation[0].Content.Should().Be("Need approval");
        fresh.HumanFeedbackActions.Should().Be("[\"approve\",\"reject\"]");
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
        fresh!.Conversation.Should().BeEmpty();
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
        fresh!.Status.Should().Be(WorkItemStatus.WaitingForIld);
        fresh.Conversation.Should().HaveCount(2);
        fresh.Conversation[1].Role.Should().Be("human");
        fresh.Conversation[1].Content.Should().Be("approve please");
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

        resp.ActiveItems.Should().ContainSingle().Which.Id.Should().Be(running.Id);
        resp.ReadyItems.Should().ContainSingle().Which.Id.Should().Be(ready.Id);

        var raw = await _db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == running.Id);
        raw.LastHeartbeatAt.Should().Be(_clock.Now);
    }

    [Fact]
    public async Task ReclaimStale_moves_unheartbeated_running_items_back_to_Ready()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });
        await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running });

        // advance time past timeout
        _clock.Now = _clock.Now.AddMinutes(20);
        var n = await _svc.ReclaimStaleAsync(TimeSpan.FromMinutes(15));

        n.Should().Be(1);
        var fresh = await _svc.GetAsync(dto.Id);
        fresh!.Status.Should().Be(WorkItemStatus.Ready);
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

        n.Should().Be(0);
        var fresh = await _svc.GetAsync(dto.Id);
        fresh!.Status.Should().Be(WorkItemStatus.Running);
    }

    [Fact]
    public async Task AddDependency_rejects_self_reference_and_unknown_targets()
    {
        var a = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" });

        (await _svc.AddDependencyAsync(a.Id, a.Id)).Should().BeFalse();
        (await _svc.AddDependencyAsync(a.Id, Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveDependency_returns_false_when_dependency_not_present()
    {
        var a = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" });

        (await _svc.RemoveDependencyAsync(a.Id, Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_filters_by_status_and_tags()
    {
        await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a", Tags = new[] { "feature" }, ForceStatus = WorkItemStatus.Ready });
        await _svc.CreateAsync(new CreateWorkItemRequest { Title = "b", Tags = new[] { "bug-fix" }, ForceStatus = WorkItemStatus.Ready });
        await _svc.CreateAsync(new CreateWorkItemRequest { Title = "c", Tags = new[] { "feature" }, ForceStatus = WorkItemStatus.Backlog });

        var list = await _svc.ListAsync(WorkItemStatus.Ready, new[] { "feature" });

        list.Should().ContainSingle().Which.Title.Should().Be("a");
    }
}
