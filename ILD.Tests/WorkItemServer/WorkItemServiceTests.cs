
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
    private DbContextOptions<WorkItemServerDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkItemServerDbContext>()
            .UseSqlite(_conn)
            .Options;
        _options = options;
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
    public void Description_is_mapped_with_no_max_length()
    {
        // The entity must map Description as unbounded text — no length cap.
        var prop = _db.Model
            .FindEntityType(typeof(WorkItem))!
            .FindProperty(nameof(WorkItem.Description))!;

        Assert.Null(prop.GetMaxLength());
    }

    [Fact]
    public async Task Create_round_trips_description_far_larger_than_old_cap()
    {
        var description = new string('x', 20000);

        var created = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "big",
            Description = description,
        });

        var fresh = await _svc.GetAsync(created.Id);
        Assert.Equal(description, fresh!.Description);
    }

    [Fact]
    public async Task Create_defaults_AiProviderOverride_to_None()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });

        Assert.Equal(AiProviderOverrideMode.None, dto.AiProviderOverride);
        Assert.Null(dto.AiProviderOverrideId);
    }

    [Fact]
    public async Task Update_round_trips_AiProviderOverride_mode_and_target()
    {
        var created = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });
        var target = Guid.NewGuid();

        var updated = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest
        {
            AiProviderOverride = AiProviderOverrideMode.OverrideAll,
            AiProviderOverrideId = target,
        });

        Assert.Equal(AiProviderOverrideMode.OverrideAll, updated!.AiProviderOverride);
        Assert.Equal(target, updated.AiProviderOverrideId);

        // Persisted, not just echoed back.
        var fresh = await _svc.GetAsync(created.Id);
        Assert.Equal(AiProviderOverrideMode.OverrideAll, fresh!.AiProviderOverride);
        Assert.Equal(target, fresh.AiProviderOverrideId);
    }

    [Fact]
    public async Task Update_clearing_override_back_to_None_drops_the_target()
    {
        var created = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });
        await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest
        {
            AiProviderOverride = AiProviderOverrideMode.OverrideDefault,
            AiProviderOverrideId = Guid.NewGuid(),
        });

        var cleared = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest
        {
            AiProviderOverride = AiProviderOverrideMode.None,
            AiProviderOverrideId = null,
        });

        Assert.Equal(AiProviderOverrideMode.None, cleared!.AiProviderOverride);
        Assert.Null(cleared.AiProviderOverrideId);
    }

    [Fact]
    public async Task Update_without_override_fields_leaves_existing_override_intact()
    {
        var created = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });
        var target = Guid.NewGuid();
        await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest
        {
            AiProviderOverride = AiProviderOverrideMode.OverrideAll,
            AiProviderOverrideId = target,
        });

        // A title-only edit (override fields null) must not disturb the override.
        var afterTitleEdit = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest
        {
            Title = "renamed",
        });

        Assert.Equal("renamed", afterTitleEdit!.Title);
        Assert.Equal(AiProviderOverrideMode.OverrideAll, afterTitleEdit.AiProviderOverride);
        Assert.Equal(target, afterTitleEdit.AiProviderOverrideId);
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
    public async Task Concurrent_claims_for_same_item_yield_exactly_one_success()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "x",
            ForceStatus = WorkItemStatus.Ready,
        });

        // Two independent clients, each its own context over the same database.
        // Both load the item while it is still Ready and hold that snapshot —
        // exactly the read-then-write window the old in-memory guard could not
        // close. EF returns the already-tracked (stale) Ready instance to each
        // service, so both clients believe the item is unclaimed when they act.
        var clientA = NewContext();
        var clientB = NewContext();
        await using var _a = clientA;
        await using var _b = clientB;
        await clientA.WorkItems.FirstAsync(w => w.Id == dto.Id);
        await clientB.WorkItems.FirstAsync(w => w.Id == dto.Id);

        var first = await new WorkItemService(clientA, _clock)
            .TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running });
        var second = await new WorkItemService(clientB, _clock)
            .TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running });

        // Exactly one wins; the loser is rejected as already claimed.
        Assert.True(first.Success);
        Assert.Equal(WorkItemStatus.Running, first.ActualStatus);
        Assert.False(second.Success);
        Assert.Equal("Already claimed", second.Reason);
        Assert.Equal(WorkItemStatus.Running, second.ActualStatus);
    }

    private WorkItemServerDbContext NewContext()
        => new(new DbContextOptionsBuilder<WorkItemServerDbContext>().UseSqlite(_conn).Options);

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
    public async Task Transition_dependency_to_Done_promotes_waiting_WorkQueue_item_to_Ready()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" });
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            ForceStatus = WorkItemStatus.WorkQueue,
            Dependencies = new[] { dep.Id },
        });

        await _svc.TransitionAsync(dep.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done });

        var fresh = await _svc.GetAsync(child.Id);
        Assert.Equal(WorkItemStatus.Ready, fresh!.Status);
    }

    [Fact]
    public async Task Transition_dependency_to_Done_leaves_item_with_unfinished_deps_in_WorkQueue()
    {
        var dep1 = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep1" });
        var dep2 = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep2" });
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            ForceStatus = WorkItemStatus.WorkQueue,
            Dependencies = new[] { dep1.Id, dep2.Id },
        });

        // Only one of the two dependencies is finished.
        await _svc.TransitionAsync(dep1.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done });

        var fresh = await _svc.GetAsync(child.Id);
        Assert.Equal(WorkItemStatus.WorkQueue, fresh!.Status);

        // Finishing the last dependency promotes it.
        await _svc.TransitionAsync(dep2.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done });

        var promoted = await _svc.GetAsync(child.Id);
        Assert.Equal(WorkItemStatus.Ready, promoted!.Status);
    }

    [Fact]
    public async Task Transition_dependency_to_Done_does_not_promote_Backlog_dependents()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" });
        // Backlog items still require human approval to enter the work queue.
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            ForceStatus = WorkItemStatus.Backlog,
            Dependencies = new[] { dep.Id },
        });

        await _svc.TransitionAsync(dep.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done });

        var fresh = await _svc.GetAsync(child.Id);
        Assert.Equal(WorkItemStatus.Backlog, fresh!.Status);
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
    public async Task Transition_with_Name_records_author_on_conversation_entry()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });

        await _svc.TransitionAsync(dto.Id, new TransitionRequest
        {
            TargetStatus = WorkItemStatus.HumanFeedback,
            Reason = "Need approval",
            Name = "Code Review",
        });

        var fresh = await _svc.GetAsync(dto.Id);
        Assert.Single(fresh!.Conversation);
        Assert.Equal("ai", fresh.Conversation[0].Role);
        Assert.Equal("Code Review", fresh.Conversation[0].Name);
    }

    [Fact]
    public async Task Transition_to_Done_with_reason_is_recorded_as_ai_role()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });

        await _svc.TransitionAsync(dto.Id, new TransitionRequest
        {
            TargetStatus = WorkItemStatus.Done,
            Reason = "All checks passed",
        });

        var fresh = await _svc.GetAsync(dto.Id);
        Assert.Single(fresh!.Conversation);
        // Done is a system/AI-authored event, not a human turn.
        Assert.Equal("ai", fresh.Conversation[0].Role);
        Assert.Equal("All checks passed", fresh.Conversation[0].Content);
    }

    [Fact]
    public async Task AppendConversation_adds_named_ai_turn_without_changing_status()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" });
        await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running });

        var ok = await _svc.AppendConversationAsync(dto.Id, "ai", "Implemented the feature", "AI Coder");

        Assert.True(ok);
        var fresh = await _svc.GetAsync(dto.Id);
        // Status is untouched — an AI turn is dialogue, not a lifecycle change.
        Assert.Equal(WorkItemStatus.Running, fresh!.Status);
        Assert.Single(fresh.Conversation);
        Assert.Equal("ai", fresh.Conversation[0].Role);
        Assert.Equal("Implemented the feature", fresh.Conversation[0].Content);
        Assert.Equal("AI Coder", fresh.Conversation[0].Name);
    }

    [Fact]
    public async Task AppendConversation_returns_false_for_missing_work_item()
    {
        var ok = await _svc.AppendConversationAsync("does-not-exist", "ai", "hi", "AI Coder");
        Assert.False(ok);
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
    public async Task Reconcile_promotes_WorkQueue_item_created_with_deps_already_done()
    {
        // #1: an item lands in WorkQueue with its dependency already complete.
        // No future Done transition will fire, so only the reconciler can rescue it.
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" });
        await _svc.TransitionAsync(dep.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done });
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            ForceStatus = WorkItemStatus.WorkQueue,
            Dependencies = new[] { dep.Id },
        });

        Assert.Equal(WorkItemStatus.WorkQueue, (await _svc.GetAsync(child.Id))!.Status);

        var n = await _svc.ReconcileWorkQueueAsync();

        Assert.Equal(1, n);
        Assert.Equal(WorkItemStatus.Ready, (await _svc.GetAsync(child.Id))!.Status);
    }

    [Fact]
    public async Task Reconcile_promotes_dependency_free_WorkQueue_item()
    {
        // A WorkQueue item with no dependencies is trivially ready; it strands
        // if it never reached WorkQueue through the client path that re-checks
        // readiness. The reconciler must promote it.
        var item = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "x",
            ForceStatus = WorkItemStatus.WorkQueue,
        });

        var n = await _svc.ReconcileWorkQueueAsync();

        Assert.Equal(1, n);
        Assert.Equal(WorkItemStatus.Ready, (await _svc.GetAsync(item.Id))!.Status);
    }

    [Fact]
    public async Task Reconcile_leaves_WorkQueue_item_with_unfinished_deps_untouched()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" });
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            ForceStatus = WorkItemStatus.WorkQueue,
            Dependencies = new[] { dep.Id },
        });

        var n = await _svc.ReconcileWorkQueueAsync();

        Assert.Equal(0, n);
        Assert.Equal(WorkItemStatus.WorkQueue, (await _svc.GetAsync(child.Id))!.Status);
    }

    [Fact]
    public async Task Reconcile_recovers_dependent_stranded_by_a_lost_promotion()
    {
        // #3/#4: simulate a crash/cancellation between the dependency's Done
        // commit and the promotion write by marking the dependency Done directly
        // on the database, bypassing TransitionAsync's promotion side effect.
        // The dependent is left stuck in WorkQueue with no retrigger.
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" });
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            ForceStatus = WorkItemStatus.WorkQueue,
            Dependencies = new[] { dep.Id },
        });

        var depRow = await _db.WorkItems.FirstAsync(w => w.Id == dep.Id);
        depRow.Status = WorkItemStatus.Done;
        await _db.SaveChangesAsync();

        Assert.Equal(WorkItemStatus.WorkQueue, (await _svc.GetAsync(child.Id))!.Status);

        var n = await _svc.ReconcileWorkQueueAsync();

        Assert.Equal(1, n);
        Assert.Equal(WorkItemStatus.Ready, (await _svc.GetAsync(child.Id))!.Status);
    }

    [Fact]
    public async Task Delete_scrubs_dependency_reference_and_promotes_now_ready_dependent()
    {
        // #2: deleting a dependency must not leave a dangling reference that
        // blocks the dependent forever. The id is scrubbed and the dependent,
        // whose only blocker is gone, is promoted to Ready.
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" });
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            ForceStatus = WorkItemStatus.WorkQueue,
            Dependencies = new[] { dep.Id },
        });

        var deleted = await _svc.DeleteAsync(dep.Id);

        Assert.True(deleted);
        var fresh = await _svc.GetAsync(child.Id);
        Assert.Empty(fresh!.Dependencies);
        Assert.Equal(WorkItemStatus.Ready, fresh.Status);
    }

    [Fact]
    public async Task Delete_scrubs_one_of_several_deps_without_promoting_still_blocked_dependent()
    {
        // Deleting one of two dependencies removes the dangling reference but
        // leaves the dependent in WorkQueue while its other dependency is open.
        var dep1 = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep1" });
        var dep2 = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep2" });
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            ForceStatus = WorkItemStatus.WorkQueue,
            Dependencies = new[] { dep1.Id, dep2.Id },
        });

        await _svc.DeleteAsync(dep1.Id);

        var fresh = await _svc.GetAsync(child.Id);
        Assert.Equal(dep2.Id, Assert.Single(fresh!.Dependencies));
        Assert.Equal(WorkItemStatus.WorkQueue, fresh.Status);

        // Finishing the remaining dependency now promotes it — no dangling ref
        // left behind to block the claim.
        await _svc.TransitionAsync(dep2.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done });
        Assert.Equal(WorkItemStatus.Ready, (await _svc.GetAsync(child.Id))!.Status);
    }

    // ──────────────────────────────────────────────────────────────────
    // Pull requests (WI-203). A PR touches the repository and is part of the
    // work item, so the server holds it — unlike the ILD instance's worktree,
    // branch and PR snapshot, which are throwaway and go with the run.
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordPullRequest_puts_the_PR_on_the_work_item()
    {
        var wi = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" });
        var runId = Guid.NewGuid();

        Assert.True(await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest
        {
            Url = "https://forgejo/repo/pulls/1",
            LoopRunId = runId,
            CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        }));

        var pr = Assert.Single((await _svc.GetAsync(wi.Id))!.PullRequests);
        Assert.Equal("https://forgejo/repo/pulls/1", pr.Url);
        Assert.Equal(runId, pr.LoopRunId);
        Assert.False(pr.Merged);
        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), pr.CreatedAt);
        // Recording a PR is not a lifecycle event.
        Assert.Equal(WorkItemStatus.Backlog, (await _svc.GetAsync(wi.Id))!.Status);
    }

    [Fact]
    public async Task RecordPullRequest_is_keyed_on_the_url_and_never_unmerges()
    {
        var wi = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" });
        const string url = "https://forgejo/repo/pulls/1";
        var opened = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        // Opened, then reported merged, then re-reported by a later run that
        // has no idea it was merged (a retry pointed back at the same PR).
        await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = url, CreatedAt = opened });
        await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = url, Merged = true, CreatedAt = opened });
        var laterRun = Guid.NewGuid();
        await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest
        {
            Url = url,
            LoopRunId = laterRun,
            CreatedAt = opened.AddHours(2),
        });

        var pr = Assert.Single((await _svc.GetAsync(wi.Id))!.PullRequests);
        Assert.True(pr.Merged);
        Assert.Equal(laterRun, pr.LoopRunId);
        // The item has had this PR since the first run opened it.
        Assert.Equal(opened, pr.CreatedAt);
    }

    [Fact]
    public async Task Pull_requests_come_back_newest_first_whatever_order_they_arrive_in()
    {
        var wi = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" });
        var day = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = "pulls/2", CreatedAt = day.AddHours(2) });
        await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = "pulls/1", CreatedAt = day.AddHours(1) });
        await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = "pulls/3", CreatedAt = day.AddHours(3) });

        Assert.Equal(
            new[] { "pulls/3", "pulls/2", "pulls/1" },
            (await _svc.GetAsync(wi.Id))!.PullRequests.Select(p => p.Url));
    }

    [Fact]
    public async Task RecordPullRequest_does_not_lose_a_PR_a_competing_writer_recorded()
    {
        var wi = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" });
        var day = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        // This scope has read the item, so it holds a copy of the list from
        // before anyone else touched it.
        await _svc.GetAsync(wi.Id);

        // Another writer — a second request, or another ILD instance
        // reconciling the same item — records a PR this one has never seen.
        await using var otherDb = new WorkItemServerDbContext(_options);
        var other = new WorkItemService(otherDb, _clock);
        Assert.True(await other.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = "pulls/1", CreatedAt = day }));

        Assert.True(await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = "pulls/2", CreatedAt = day.AddHours(1) }));

        // Both survive: recording a PR reads the list as it stands and writes
        // against that snapshot, rather than overwriting it from a stale copy.
        Assert.Equal(
            new[] { "pulls/2", "pulls/1" },
            (await other.GetAsync(wi.Id))!.PullRequests.Select(p => p.Url));
        // ...and this scope's own view of the item agrees.
        Assert.Equal(2, (await _svc.GetAsync(wi.Id))!.PullRequests.Count);
    }

    [Fact]
    public async Task RecordPullRequest_reports_an_unknown_work_item()
    {
        Assert.False(await _svc.RecordPullRequestAsync("WI-nope", new RecordPullRequestRequest { Url = "pulls/1" }));
    }

    [Fact]
    public async Task Deleting_a_work_item_takes_its_pull_requests_with_it()
    {
        var wi = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" });
        await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = "pulls/1" });

        Assert.True(await _svc.DeleteAsync(wi.Id));

        Assert.Null(await _svc.GetAsync(wi.Id));
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
