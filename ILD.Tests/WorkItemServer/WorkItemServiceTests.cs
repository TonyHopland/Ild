
using System.Data.Common;
using ILD.WorkItemServer;
using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Dtos;
using ILD.WorkItemServer.Services;
using ILD.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ILD.Tests.WorkItemServer;

public class WorkItemServiceTests : IAsyncLifetime
{
    private SqliteConnection _conn = null!;
    private WorkItemServerDbContext _db = null!;
    private TestClock _clock = null!;
    private WorkItemService _svc = null!;
    private DbContextOptions<WorkItemServerDbContext> _options = null!;

    public async ValueTask InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        var options = new DbContextOptionsBuilder<WorkItemServerDbContext>()
            .UseSqlite(_conn)
            .Options;
        _options = options;
        _db = new WorkItemServerDbContext(options) { HistoryActor = TestActors.ServerHuman };
        await _db.Database.EnsureCreatedAsync();
        _clock = new TestClock(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        _svc = new WorkItemService(_db, _clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    [Fact]
    public async Task Create_defaults_to_Backlog_and_persists_tags_and_dependencies()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, TestContext.Current.CancellationToken);
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            Description = "d",
            Tags = new[] { "bug-fix" },
            Dependencies = new[] { dep.Id },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(WorkItemStatus.Backlog, dto.Status);
        Assert.Equal("bug-fix", Assert.Single(dto.Tags));
        Assert.Equal(dep.Id, Assert.Single(dto.Dependencies));
    }

    [Fact]
    public async Task A_new_item_goes_above_every_existing_item_of_any_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "first" }, ct);
        Assert.Equal(0d, first.Priority);

        var second = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "second", ForceStatus = WorkItemStatus.Ready }, ct);
        Assert.True(second.Priority < first.Priority);

        var done = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "done", ForceStatus = WorkItemStatus.Done }, ct);
        await _svc.UpdateAsync(done.Id, new UpdateWorkItemRequest { Priority = -1000.5 }, ct);

        var third = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "third" }, ct);

        Assert.True(third.Priority < -1000.5, $"third got {third.Priority}");
        Assert.Equal(third.Priority, (await _svc.GetAsync(third.Id, ct))!.Priority);
    }


    [Fact]
    public async Task An_item_moved_to_Done_goes_above_every_other_item_and_later_moves_stack_in_reverse_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var other = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "other", ForceStatus = WorkItemStatus.Ready }, ct);
        var a = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" }, ct);
        var b = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "b" }, ct);
        var c = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "c" }, ct);
        var oldDone = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "old", ForceStatus = WorkItemStatus.Done }, ct);
        await _svc.UpdateAsync(oldDone.Id, new UpdateWorkItemRequest { Priority = -500 }, ct);

        foreach (var id in new[] { a.Id, b.Id, c.Id })
            Assert.True((await _svc.TransitionAsync(id, new TransitionRequest { TargetStatus = WorkItemStatus.Done }, ct)).Success);

        var board = (await _svc.ListAsync(null, null, ct)).ToList();
        var doneOrder = board.Where(w => w.Status == WorkItemStatus.Done).Select(w => w.Id).ToList();
        Assert.Equal(new[] { c.Id, b.Id, a.Id, oldDone.Id }, doneOrder);
        Assert.Equal(c.Id, board[0].Id);
    }

    [Fact]
    public async Task A_transition_that_is_not_a_move_into_Done_or_is_refused_leaves_priority_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, ct);
        var blocked = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "blocked", Dependencies = new[] { dep.Id } }, ct);
        var done = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "done", ForceStatus = WorkItemStatus.Done }, ct);
        await _svc.UpdateAsync(done.Id, new UpdateWorkItemRequest { Priority = 7 }, ct);
        var mover = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "mover" }, ct);
        await _svc.UpdateAsync(mover.Id, new UpdateWorkItemRequest { Priority = 3 }, ct);

        await _svc.TransitionAsync(done.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done }, ct);
        await _svc.TransitionAsync(mover.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Ready }, ct);
        var refused = await _svc.TransitionAsync(blocked.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running }, ct);

        Assert.False(refused.Success);
        Assert.Equal(7, (await _svc.GetAsync(done.Id, ct))!.Priority);
        Assert.Equal(3, (await _svc.GetAsync(mover.Id, ct))!.Priority);
        Assert.Equal(blocked.Priority, (await _svc.GetAsync(blocked.Id, ct))!.Priority);
    }

    [Fact]
    public async Task Moving_an_item_to_Done_still_promotes_dependents_and_writes_no_priority_history()
    {
        var ct = TestContext.Current.CancellationToken;
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, ct);
        var child = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "child", ForceStatus = WorkItemStatus.WorkQueue, Dependencies = new[] { dep.Id } }, ct);

        await _svc.TransitionAsync(dep.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done }, ct);

        Assert.Equal(WorkItemStatus.Ready, (await _svc.GetAsync(child.Id, ct))!.Status);
        Assert.True((await _svc.GetAsync(dep.Id, ct))!.Priority < dep.Priority);
        var history = (await _svc.GetHistoryAsync(dep.Id, ct))!;
        Assert.Contains(history, e => e.Field == WorkItemHistoryFields.Status && e.NewValue == "Done");
        Assert.DoesNotContain(history, e => e.Field.Equals(nameof(WorkItem.Priority), StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public async Task Update_sets_the_priority_when_given_and_leaves_it_alone_otherwise()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, ct);
        _clock.Now = _clock.Now.AddMinutes(1);

        var moved = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest { Priority = 2.5 }, ct);

        Assert.Equal(2.5, moved!.Priority);
        Assert.Equal(_clock.Now, moved.UpdatedAt);
        Assert.Equal("x", moved.Title);

        var renamed = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest { Title = "y" }, ct);

        Assert.Equal(2.5, renamed!.Priority);
        Assert.Equal(2.5, (await _svc.GetAsync(created.Id, ct))!.Priority);
    }

    /// <summary>
    /// Ids "2" and "10" tie on priority: the numeric id order puts "2" first,
    /// where an ordinal string comparison or a descending one would not.
    /// </summary>
    [Fact]
    public async Task List_and_poll_return_items_in_board_order_with_equal_priorities_in_id_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var ids = new List<string>();
        for (var i = 1; i <= 10; i++)
            ids.Add((await _svc.CreateAsync(new CreateWorkItemRequest { Title = $"i{i}", ForceStatus = WorkItemStatus.Ready }, ct)).Id);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), ids);

        var priorities = new Dictionary<string, double>
        {
            ["1"] = 0.5, ["2"] = 1, ["3"] = -1, ["4"] = 7, ["5"] = 7,
            ["6"] = 7, ["7"] = 7, ["8"] = 7, ["9"] = 7, ["10"] = 1,
        };
        foreach (var (id, priority) in priorities)
            await _svc.UpdateAsync(id, new UpdateWorkItemRequest { Priority = priority }, ct);
        await _svc.TransitionAsync("9", new TransitionRequest { TargetStatus = WorkItemStatus.Backlog }, ct);
        _db.ChangeTracker.Clear();

        var expected = new[] { "3", "1", "2", "10", "4", "5", "6", "7", "8", "9" };
        Assert.Equal(expected, (await _svc.ListAsync(null, null, ct)).Select(w => w.Id));
        Assert.Equal(expected.Where(id => id != "9"), (await _svc.ListAsync(WorkItemStatus.Ready, null, ct)).Select(w => w.Id));
        Assert.Equal(expected.Where(id => id != "9"), (await _svc.PollAsync(Array.Empty<string>(), ct)).ReadyItems.Select(w => w.Id));
    }

    /// <summary>
    /// SQLite returns rows tied on every sort key in key order, which Postgres
    /// does not promise, so a sort on the priority alone can only be seen in the
    /// query: wherever the database sorts by priority, a further key follows it.
    /// </summary>
    [Fact]
    public async Task Wherever_the_database_sorts_by_priority_a_tie_break_follows()
    {
        var ct = TestContext.Current.CancellationToken;
        await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a", ForceStatus = WorkItemStatus.Ready }, ct);
        await _svc.CreateAsync(new CreateWorkItemRequest { Title = "b", ForceStatus = WorkItemStatus.Ready }, ct);
        var commands = new CommandTextRecorder();
        await using var recorded = new WorkItemServerDbContext(
            new DbContextOptionsBuilder<WorkItemServerDbContext>().UseSqlite(_conn).AddInterceptors(commands).Options)
        {
            HistoryActor = TestActors.ServerHuman,
        };
        var svc = new WorkItemService(recorded, _clock);

        await svc.ListAsync(null, null, ct);
        await svc.ListAsync(WorkItemStatus.Ready, null, ct);
        await svc.PollAsync(Array.Empty<string>(), ct);

        foreach (var command in commands.Executed)
        {
            var orderBy = System.Text.RegularExpressions.Regex.Match(command, @"ORDER BY (?<keys>.*?)(LIMIT|OFFSET|\)|;|$)",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (orderBy.Success && orderBy.Groups["keys"].Value.Contains("\"Priority\"", StringComparison.Ordinal))
                Assert.True(orderBy.Groups["keys"].Value.Contains(',', StringComparison.Ordinal), command);
        }
    }

    /// <summary>
    /// Every way an item changes column without a drag keeps its order value, so
    /// items keep their order relative to each other: a transition, the Running
    /// claim, a stale reclaim, a promotion when a dependency is done, and the
    /// scrub of a deleted dependency.
    /// </summary>
    [Fact]
    public async Task Status_changes_never_change_the_priority()
    {
        var ct = TestContext.Current.CancellationToken;
        var doneDep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep done later" }, ct);
        var deletedDep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep deleted later" }, ct);
        var a = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" }, ct);
        var b = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "b", ForceStatus = WorkItemStatus.WorkQueue, Dependencies = new[] { doneDep.Id },
        }, ct);
        var c = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "c", ForceStatus = WorkItemStatus.WorkQueue, Dependencies = new[] { deletedDep.Id },
        }, ct);
        var set = new Dictionary<string, double> { [a.Id] = 3.25, [b.Id] = -7.5, [c.Id] = 11 };
        foreach (var (id, priority) in set)
            await _svc.UpdateAsync(id, new UpdateWorkItemRequest { Priority = priority }, ct);

        async Task AssertKeptAsync()
        {
            _db.ChangeTracker.Clear();
            foreach (var (id, priority) in set)
                Assert.Equal(priority, (await _svc.GetAsync(id, ct))!.Priority);
        }

        await _svc.TransitionAsync(a.Id, new TransitionRequest { TargetStatus = WorkItemStatus.WorkQueue }, ct);
        await _svc.TransitionAsync(a.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Ready }, ct);
        await AssertKeptAsync();
        Assert.True((await _svc.TransitionAsync(a.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running }, ct)).Success);
        await AssertKeptAsync();
        _clock.Now = _clock.Now.AddMinutes(20);
        Assert.Equal(1, await _svc.ReclaimStaleAsync(TimeSpan.FromMinutes(15), ct));
        await AssertKeptAsync();

        await _svc.TransitionAsync(doneDep.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done }, ct);
        Assert.True(await _svc.DeleteAsync(deletedDep.Id, ct));
        await AssertKeptAsync();

        foreach (var id in new[] { a.Id, b.Id, c.Id })
            Assert.Equal(WorkItemStatus.Ready, (await _svc.GetAsync(id, ct))!.Status);
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
    public async Task Create_defaults_AiProviderOverride_to_None()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, TestContext.Current.CancellationToken);

        Assert.Equal(AiProviderOverrideMode.None, dto.AiProviderOverride);
        Assert.Null(dto.AiProviderOverrideId);
    }

    [Fact]
    public async Task Update_round_trips_AiProviderOverride_mode_and_target()
    {
        var created = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, TestContext.Current.CancellationToken);
        var target = Guid.NewGuid();

        var updated = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest
        {
            AiProviderOverride = AiProviderOverrideMode.OverrideAll,
            AiProviderOverrideId = target,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(AiProviderOverrideMode.OverrideAll, updated!.AiProviderOverride);
        Assert.Equal(target, updated.AiProviderOverrideId);

        // Persisted, not just echoed back.
        var fresh = await _svc.GetAsync(created.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AiProviderOverrideMode.OverrideAll, fresh!.AiProviderOverride);
        Assert.Equal(target, fresh.AiProviderOverrideId);
    }

    [Fact]
    public async Task Update_clearing_override_back_to_None_drops_the_target()
    {
        var created = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, TestContext.Current.CancellationToken);
        await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest
        {
            AiProviderOverride = AiProviderOverrideMode.OverrideDefault,
            AiProviderOverrideId = Guid.NewGuid(),
        }, TestContext.Current.CancellationToken);

        var cleared = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest
        {
            AiProviderOverride = AiProviderOverrideMode.None,
            AiProviderOverrideId = null,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(AiProviderOverrideMode.None, cleared!.AiProviderOverride);
        Assert.Null(cleared.AiProviderOverrideId);
    }

    [Fact]
    public async Task Update_without_override_fields_leaves_existing_override_intact()
    {
        var created = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, TestContext.Current.CancellationToken);
        var target = Guid.NewGuid();
        await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest
        {
            AiProviderOverride = AiProviderOverrideMode.OverrideAll,
            AiProviderOverrideId = target,
        }, TestContext.Current.CancellationToken);

        // A title-only edit (override fields null) must not disturb the override.
        var afterTitleEdit = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest
        {
            Title = "renamed",
        }, TestContext.Current.CancellationToken);

        Assert.Equal("renamed", afterTitleEdit!.Title);
        Assert.Equal(AiProviderOverrideMode.OverrideAll, afterTitleEdit.AiProviderOverride);
        Assert.Equal(target, afterTitleEdit.AiProviderOverrideId);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  feature/foo  ", "feature/foo")]
    public async Task Create_stores_the_custom_branch_name_trimmed_with_blank_meaning_none(string? sent, string? expected)
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x", BranchNameOverride = sent }, TestContext.Current.CancellationToken);

        Assert.Equal(expected, dto.BranchNameOverride);
        Assert.Equal(expected, (await _svc.GetAsync(dto.Id, TestContext.Current.CancellationToken))!.BranchNameOverride);
    }

    [Fact]
    public async Task Update_sets_clears_and_leaves_the_custom_branch_name_alone()
    {
        var created = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, TestContext.Current.CancellationToken);

        var set = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest { BranchNameOverride = "feature/foo" }, TestContext.Current.CancellationToken);
        Assert.Equal("feature/foo", set!.BranchNameOverride);

        // Null means "not part of this edit" — a title-only save must not drop it.
        var titleOnly = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest { Title = "renamed" }, TestContext.Current.CancellationToken);
        Assert.Equal("feature/foo", titleOnly!.BranchNameOverride);

        // Blank is a deliberate "go back to generated per-run naming".
        var cleared = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest { BranchNameOverride = "" }, TestContext.Current.CancellationToken);
        Assert.Null(cleared!.BranchNameOverride);
        Assert.Null((await _svc.GetAsync(created.Id, TestContext.Current.CancellationToken))!.BranchNameOverride);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  release/1.0  ", "release/1.0")]
    public async Task Create_stores_the_base_branch_trimmed_with_blank_meaning_none(string? sent, string? expected)
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x", BaseBranchOverride = sent }, TestContext.Current.CancellationToken);

        Assert.Equal(expected, dto.BaseBranchOverride);
        Assert.Equal(expected, (await _svc.GetAsync(dto.Id, TestContext.Current.CancellationToken))!.BaseBranchOverride);
    }

    [Fact]
    public async Task Update_sets_clears_and_leaves_the_base_branch_alone()
    {
        var created = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, TestContext.Current.CancellationToken);

        var set = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest { BaseBranchOverride = "release/1.0" }, TestContext.Current.CancellationToken);
        Assert.Equal("release/1.0", set!.BaseBranchOverride);

        // Null means "not part of this edit" — a title-only save must not drop it.
        var titleOnly = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest { Title = "renamed" }, TestContext.Current.CancellationToken);
        Assert.Equal("release/1.0", titleOnly!.BaseBranchOverride);

        // Blank is a deliberate "go back to the repository's default branch".
        var cleared = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest { BaseBranchOverride = "" }, TestContext.Current.CancellationToken);
        Assert.Null(cleared!.BaseBranchOverride);
        Assert.Null((await _svc.GetAsync(created.Id, TestContext.Current.CancellationToken))!.BaseBranchOverride);
    }

    [Fact]
    public async Task Update_replaces_the_repository_and_leaves_it_alone_when_null()
    {
        var repoA = Guid.NewGuid();
        var repoB = Guid.NewGuid();
        var created = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x", RepositoryId = repoA }, TestContext.Current.CancellationToken);

        var moved = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest { RepositoryId = repoB }, TestContext.Current.CancellationToken);
        Assert.Equal(repoB, moved!.RepositoryId);

        var titleOnly = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest { Title = "renamed" }, TestContext.Current.CancellationToken);
        Assert.Equal(repoB, titleOnly!.RepositoryId);
        Assert.Equal(repoB, (await _svc.GetAsync(created.Id, TestContext.Current.CancellationToken))!.RepositoryId);
    }

    [Fact]
    public async Task The_two_branch_overrides_are_stored_independently()
    {
        // They travel the same plumbing and normalise the same way; clearing one
        // must not touch the other.
        var created = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "x",
            BranchNameOverride = "feature/foo",
            BaseBranchOverride = "release/1.0",
        }, TestContext.Current.CancellationToken);

        var cleared = await _svc.UpdateAsync(created.Id, new UpdateWorkItemRequest { BranchNameOverride = "" }, TestContext.Current.CancellationToken);

        Assert.Null(cleared!.BranchNameOverride);
        Assert.Equal("release/1.0", cleared.BaseBranchOverride);
    }

    [Fact]
    public async Task Create_honours_forceStatus_when_provided()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "x",
            ForceStatus = WorkItemStatus.Ready,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(WorkItemStatus.Ready, dto.Status);
    }

    [Fact]
    public async Task Transition_to_Running_succeeds_when_no_dependencies()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, TestContext.Current.CancellationToken);

        var resp = await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running }, TestContext.Current.CancellationToken);

        Assert.True(resp.Success);
        Assert.Equal(WorkItemStatus.Running, resp.ActualStatus);
    }

    [Fact]
    public async Task Transition_to_Running_fails_when_already_Running()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, TestContext.Current.CancellationToken);
        await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running }, TestContext.Current.CancellationToken);

        var second = await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running }, TestContext.Current.CancellationToken);

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
        }, TestContext.Current.CancellationToken);

        // Two independent clients, each its own context over the same database.
        // Both load the item while it is still Ready and hold that snapshot —
        // exactly the read-then-write window the old in-memory guard could not
        // close. EF returns the already-tracked (stale) Ready instance to each
        // service, so both clients believe the item is unclaimed when they act.
        var clientA = NewContext();
        var clientB = NewContext();
        await using var _a = clientA;
        await using var _b = clientB;
        await clientA.WorkItems.FirstAsync(w => w.Id == dto.Id, TestContext.Current.CancellationToken);
        await clientB.WorkItems.FirstAsync(w => w.Id == dto.Id, TestContext.Current.CancellationToken);

        var first = await new WorkItemService(clientA, _clock)
            .TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running }, TestContext.Current.CancellationToken);
        var second = await new WorkItemService(clientB, _clock)
            .TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running }, TestContext.Current.CancellationToken);

        // Exactly one wins; the loser is rejected as already claimed.
        Assert.True(first.Success);
        Assert.Equal(WorkItemStatus.Running, first.ActualStatus);
        Assert.False(second.Success);
        Assert.Equal("Already claimed", second.Reason);
        Assert.Equal(WorkItemStatus.Running, second.ActualStatus);
    }

    private WorkItemServerDbContext NewContext()
        => new(new DbContextOptionsBuilder<WorkItemServerDbContext>().UseSqlite(_conn).Options) { HistoryActor = TestActors.ServerHuman };

    [Fact]
    public async Task Transition_to_Running_fails_when_dependency_not_done()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, TestContext.Current.CancellationToken);
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            Dependencies = new[] { dep.Id },
        }, TestContext.Current.CancellationToken);

        var resp = await _svc.TransitionAsync(child.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running }, TestContext.Current.CancellationToken);

        Assert.False(resp.Success);
        Assert.Equal("Dependencies not satisfied", resp.Reason);
    }

    [Fact]
    public async Task Transition_to_Running_succeeds_after_dependency_done()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, TestContext.Current.CancellationToken);
        await _svc.TransitionAsync(dep.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done }, TestContext.Current.CancellationToken);
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            Dependencies = new[] { dep.Id },
        }, TestContext.Current.CancellationToken);

        var resp = await _svc.TransitionAsync(child.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running }, TestContext.Current.CancellationToken);

        Assert.True(resp.Success);
    }

    [Fact]
    public async Task Transition_dependency_to_Done_promotes_waiting_WorkQueue_item_to_Ready()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, TestContext.Current.CancellationToken);
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            ForceStatus = WorkItemStatus.WorkQueue,
            Dependencies = new[] { dep.Id },
        }, TestContext.Current.CancellationToken);

        await _svc.TransitionAsync(dep.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done }, TestContext.Current.CancellationToken);

        var fresh = await _svc.GetAsync(child.Id, TestContext.Current.CancellationToken);
        Assert.Equal(WorkItemStatus.Ready, fresh!.Status);
    }

    [Fact]
    public async Task Transition_dependency_to_Done_leaves_item_with_unfinished_deps_in_WorkQueue()
    {
        var dep1 = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep1" }, TestContext.Current.CancellationToken);
        var dep2 = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep2" }, TestContext.Current.CancellationToken);
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            ForceStatus = WorkItemStatus.WorkQueue,
            Dependencies = new[] { dep1.Id, dep2.Id },
        }, TestContext.Current.CancellationToken);

        // Only one of the two dependencies is finished.
        await _svc.TransitionAsync(dep1.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done }, TestContext.Current.CancellationToken);

        var fresh = await _svc.GetAsync(child.Id, TestContext.Current.CancellationToken);
        Assert.Equal(WorkItemStatus.WorkQueue, fresh!.Status);

        // Finishing the last dependency promotes it.
        await _svc.TransitionAsync(dep2.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done }, TestContext.Current.CancellationToken);

        var promoted = await _svc.GetAsync(child.Id, TestContext.Current.CancellationToken);
        Assert.Equal(WorkItemStatus.Ready, promoted!.Status);
    }

    [Fact]
    public async Task Transition_dependency_to_Done_does_not_promote_Backlog_dependents()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, TestContext.Current.CancellationToken);
        // Backlog items still require human approval to enter the work queue.
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            ForceStatus = WorkItemStatus.Backlog,
            Dependencies = new[] { dep.Id },
        }, TestContext.Current.CancellationToken);

        await _svc.TransitionAsync(dep.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done }, TestContext.Current.CancellationToken);

        var fresh = await _svc.GetAsync(child.Id, TestContext.Current.CancellationToken);
        Assert.Equal(WorkItemStatus.Backlog, fresh!.Status);
    }

    [Fact]
    public async Task Transition_to_HumanFeedback_records_the_offered_actions()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, TestContext.Current.CancellationToken);

        await _svc.TransitionAsync(dto.Id, new TransitionRequest
        {
            TargetStatus = WorkItemStatus.HumanFeedback,
            Actions = "[\"approve\",\"reject\"]",
        }, TestContext.Current.CancellationToken);

        var fresh = await _svc.GetAsync(dto.Id, TestContext.Current.CancellationToken);
        Assert.Equal(WorkItemStatus.HumanFeedback, fresh!.Status);
        Assert.Equal("[\"approve\",\"reject\"]", fresh.HumanFeedbackActions);
    }

    [Fact]
    public async Task Poll_returns_active_items_and_ready_items_and_refreshes_heartbeat()
    {
        var ready = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "ready",
            ForceStatus = WorkItemStatus.Ready,
        }, TestContext.Current.CancellationToken);
        var running = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "running" }, TestContext.Current.CancellationToken);
        await _svc.TransitionAsync(running.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running }, TestContext.Current.CancellationToken);

        // advance time to verify heartbeat is updated
        _clock.Now = _clock.Now.AddMinutes(5);
        var resp = await _svc.PollAsync(new[] { running.Id }, TestContext.Current.CancellationToken);

        Assert.Equal(running.Id, Assert.Single(resp.ActiveItems).Id);
        Assert.Equal(ready.Id, Assert.Single(resp.ReadyItems).Id);

        var raw = await _db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == running.Id, TestContext.Current.CancellationToken);
        Assert.Equal(_clock.Now, raw.LastHeartbeatAt);
    }

    [Fact]
    public async Task ReclaimStale_moves_unheartbeated_running_items_back_to_Ready()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, TestContext.Current.CancellationToken);
        await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running }, TestContext.Current.CancellationToken);

        // advance time past timeout
        _clock.Now = _clock.Now.AddMinutes(20);
        var n = await _svc.ReclaimStaleAsync(TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        Assert.Equal(1, n);
        var fresh = await _svc.GetAsync(dto.Id, TestContext.Current.CancellationToken);
        Assert.Equal(WorkItemStatus.Ready, fresh!.Status);
    }

    [Fact]
    public async Task ReclaimStale_does_not_touch_recently_heartbeated_items()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, TestContext.Current.CancellationToken);
        await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running }, TestContext.Current.CancellationToken);

        _clock.Now = _clock.Now.AddMinutes(5);
        await _svc.PollAsync(new[] { dto.Id }, TestContext.Current.CancellationToken);

        _clock.Now = _clock.Now.AddMinutes(5);
        var n = await _svc.ReclaimStaleAsync(TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        Assert.Equal(0, n);
        var fresh = await _svc.GetAsync(dto.Id, TestContext.Current.CancellationToken);
        Assert.Equal(WorkItemStatus.Running, fresh!.Status);
    }

    [Fact]
    public async Task ReclaimStale_never_reclaims_HumanFeedback_items_to_Ready()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, TestContext.Current.CancellationToken);
        await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running }, TestContext.Current.CancellationToken);
        await _svc.TransitionAsync(dto.Id, new TransitionRequest
        {
            TargetStatus = WorkItemStatus.HumanFeedback,
        }, TestContext.Current.CancellationToken);

        // advance time far past timeout
        _clock.Now = _clock.Now.AddMinutes(30);
        var n = await _svc.ReclaimStaleAsync(TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken);

        Assert.Equal(0, n);
        var fresh = await _svc.GetAsync(dto.Id, TestContext.Current.CancellationToken);
        Assert.Equal(WorkItemStatus.HumanFeedback, fresh!.Status);
    }

    [Fact]
    public async Task AddDependency_rejects_self_reference_and_unknown_targets()
    {
        var a = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" }, TestContext.Current.CancellationToken);

        Assert.False((await _svc.AddDependencyAsync(a.Id, a.Id, TestContext.Current.CancellationToken)));
        Assert.False((await _svc.AddDependencyAsync(a.Id, Guid.NewGuid().ToString(), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task RemoveDependency_returns_false_when_dependency_not_present()
    {
        var a = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" }, TestContext.Current.CancellationToken);

        Assert.False((await _svc.RemoveDependencyAsync(a.Id, Guid.NewGuid().ToString(), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Reconcile_promotes_WorkQueue_item_created_with_deps_already_done()
    {
        // #1: an item lands in WorkQueue with its dependency already complete.
        // No future Done transition will fire, so only the reconciler can rescue it.
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, TestContext.Current.CancellationToken);
        await _svc.TransitionAsync(dep.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done }, TestContext.Current.CancellationToken);
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            ForceStatus = WorkItemStatus.WorkQueue,
            Dependencies = new[] { dep.Id },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(WorkItemStatus.WorkQueue, (await _svc.GetAsync(child.Id, TestContext.Current.CancellationToken))!.Status);

        var n = await _svc.ReconcileWorkQueueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, n);
        Assert.Equal(WorkItemStatus.Ready, (await _svc.GetAsync(child.Id, TestContext.Current.CancellationToken))!.Status);
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
        }, TestContext.Current.CancellationToken);

        var n = await _svc.ReconcileWorkQueueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, n);
        Assert.Equal(WorkItemStatus.Ready, (await _svc.GetAsync(item.Id, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task Reconcile_leaves_WorkQueue_item_with_unfinished_deps_untouched()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, TestContext.Current.CancellationToken);
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            ForceStatus = WorkItemStatus.WorkQueue,
            Dependencies = new[] { dep.Id },
        }, TestContext.Current.CancellationToken);

        var n = await _svc.ReconcileWorkQueueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, n);
        Assert.Equal(WorkItemStatus.WorkQueue, (await _svc.GetAsync(child.Id, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task Reconcile_recovers_dependent_stranded_by_a_lost_promotion()
    {
        // #3/#4: simulate a crash/cancellation between the dependency's Done
        // commit and the promotion write by marking the dependency Done directly
        // on the database, bypassing TransitionAsync's promotion side effect.
        // The dependent is left stuck in WorkQueue with no retrigger.
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, TestContext.Current.CancellationToken);
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            ForceStatus = WorkItemStatus.WorkQueue,
            Dependencies = new[] { dep.Id },
        }, TestContext.Current.CancellationToken);

        var depRow = await _db.WorkItems.FirstAsync(w => w.Id == dep.Id, TestContext.Current.CancellationToken);
        depRow.Status = WorkItemStatus.Done;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WorkItemStatus.WorkQueue, (await _svc.GetAsync(child.Id, TestContext.Current.CancellationToken))!.Status);

        var n = await _svc.ReconcileWorkQueueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, n);
        Assert.Equal(WorkItemStatus.Ready, (await _svc.GetAsync(child.Id, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task Delete_scrubs_dependency_reference_and_promotes_now_ready_dependent()
    {
        // #2: deleting a dependency must not leave a dangling reference that
        // blocks the dependent forever. The id is scrubbed and the dependent,
        // whose only blocker is gone, is promoted to Ready.
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, TestContext.Current.CancellationToken);
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            ForceStatus = WorkItemStatus.WorkQueue,
            Dependencies = new[] { dep.Id },
        }, TestContext.Current.CancellationToken);

        var deleted = await _svc.DeleteAsync(dep.Id, TestContext.Current.CancellationToken);

        Assert.True(deleted);
        var fresh = await _svc.GetAsync(child.Id, TestContext.Current.CancellationToken);
        Assert.Empty(fresh!.Dependencies);
        Assert.Equal(WorkItemStatus.Ready, fresh.Status);
    }

    [Fact]
    public async Task Delete_scrubs_one_of_several_deps_without_promoting_still_blocked_dependent()
    {
        // Deleting one of two dependencies removes the dangling reference but
        // leaves the dependent in WorkQueue while its other dependency is open.
        var dep1 = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep1" }, TestContext.Current.CancellationToken);
        var dep2 = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep2" }, TestContext.Current.CancellationToken);
        var child = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "child",
            ForceStatus = WorkItemStatus.WorkQueue,
            Dependencies = new[] { dep1.Id, dep2.Id },
        }, TestContext.Current.CancellationToken);

        await _svc.DeleteAsync(dep1.Id, TestContext.Current.CancellationToken);

        var fresh = await _svc.GetAsync(child.Id, TestContext.Current.CancellationToken);
        Assert.Equal(dep2.Id, Assert.Single(fresh!.Dependencies));
        Assert.Equal(WorkItemStatus.WorkQueue, fresh.Status);

        // Finishing the remaining dependency now promotes it — no dangling ref
        // left behind to block the claim.
        await _svc.TransitionAsync(dep2.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done }, TestContext.Current.CancellationToken);
        Assert.Equal(WorkItemStatus.Ready, (await _svc.GetAsync(child.Id, TestContext.Current.CancellationToken))!.Status);
    }

    // ──────────────────────────────────────────────────────────────────
    // Pull requests (WI-203). A PR touches the repository and is part of the
    // work item, so the server holds it — unlike the ILD instance's worktree,
    // branch and PR snapshot, which are throwaway and go with the run.
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordPullRequest_puts_the_PR_on_the_work_item()
    {
        var wi = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" }, TestContext.Current.CancellationToken);
        var runId = Guid.NewGuid();

        Assert.Equal(RecordPullRequestOutcome.Recorded, await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest
        {
            Url = "https://forgejo/repo/pulls/1",
            LoopRunId = runId,
            CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        }, TestContext.Current.CancellationToken));

        var pr = Assert.Single((await _svc.GetAsync(wi.Id, TestContext.Current.CancellationToken))!.PullRequests);
        Assert.Equal("https://forgejo/repo/pulls/1", pr.Url);
        Assert.Equal(runId, pr.LoopRunId);
        Assert.False(pr.Merged);
        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), pr.CreatedAt);
        // Recording a PR is not a lifecycle event.
        Assert.Equal(WorkItemStatus.Backlog, (await _svc.GetAsync(wi.Id, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task RecordPullRequest_is_keyed_on_the_url_and_never_unmerges()
    {
        var wi = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" }, TestContext.Current.CancellationToken);
        const string url = "https://forgejo/repo/pulls/1";
        var opened = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        // Opened, then reported merged, then re-reported by a later run that
        // has no idea it was merged (a retry pointed back at the same PR).
        await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = url, CreatedAt = opened }, TestContext.Current.CancellationToken);
        await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = url, Merged = true, CreatedAt = opened }, TestContext.Current.CancellationToken);
        var laterRun = Guid.NewGuid();
        await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest
        {
            Url = url,
            LoopRunId = laterRun,
            CreatedAt = opened.AddHours(2),
        }, TestContext.Current.CancellationToken);

        var pr = Assert.Single((await _svc.GetAsync(wi.Id, TestContext.Current.CancellationToken))!.PullRequests);
        Assert.True(pr.Merged);
        Assert.Equal(laterRun, pr.LoopRunId);
        // The item has had this PR since the first run opened it.
        Assert.Equal(opened, pr.CreatedAt);
    }

    [Fact]
    public async Task Pull_requests_come_back_newest_first_whatever_order_they_arrive_in()
    {
        var wi = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" }, TestContext.Current.CancellationToken);
        var day = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = "pulls/2", CreatedAt = day.AddHours(2) }, TestContext.Current.CancellationToken);
        await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = "pulls/1", CreatedAt = day.AddHours(1) }, TestContext.Current.CancellationToken);
        await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = "pulls/3", CreatedAt = day.AddHours(3) }, TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { "pulls/3", "pulls/2", "pulls/1" },
            (await _svc.GetAsync(wi.Id, TestContext.Current.CancellationToken))!.PullRequests.Select(p => p.Url));
    }

    [Fact]
    public async Task RecordPullRequest_does_not_lose_a_PR_a_competing_writer_recorded()
    {
        var wi = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" }, TestContext.Current.CancellationToken);
        var day = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        // This scope has read the item, so it holds a copy of the list from
        // before anyone else touched it.
        await _svc.GetAsync(wi.Id, TestContext.Current.CancellationToken);

        // Another writer — a second request, or another ILD instance
        // reconciling the same item — records a PR this one has never seen.
        await using var otherDb = new WorkItemServerDbContext(_options) { HistoryActor = TestActors.ServerHuman };
        var other = new WorkItemService(otherDb, _clock);
        Assert.Equal(RecordPullRequestOutcome.Recorded, await other.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = "pulls/1", CreatedAt = day }, TestContext.Current.CancellationToken));

        Assert.Equal(RecordPullRequestOutcome.Recorded, await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = "pulls/2", CreatedAt = day.AddHours(1) }, TestContext.Current.CancellationToken));

        // Both survive: recording a PR reads the list as it stands and writes
        // against that snapshot, rather than overwriting it from a stale copy.
        Assert.Equal(
            new[] { "pulls/2", "pulls/1" },
            (await other.GetAsync(wi.Id, TestContext.Current.CancellationToken))!.PullRequests.Select(p => p.Url));
        // ...and this scope's own view of the item agrees.
        Assert.Equal(2, (await _svc.GetAsync(wi.Id, TestContext.Current.CancellationToken))!.PullRequests.Count);
    }

    [Fact]
    public async Task An_unreadable_pull_request_list_reads_as_empty_and_can_be_rebuilt()
    {
        var wi = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" }, TestContext.Current.CancellationToken);
        var row = await _db.WorkItems.FirstAsync(x => x.Id == wi.Id, TestContext.Current.CancellationToken);
        row.PullRequestsJson = "{ this is not the list";
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Every read of the work item goes through this list, so an unreadable
        // one must not take the item — or the board listing it — down.
        Assert.Empty((await _svc.GetAsync(wi.Id, TestContext.Current.CancellationToken))!.PullRequests);
        Assert.Empty(Assert.Single(await _svc.ListAsync(null, null, TestContext.Current.CancellationToken)).PullRequests);

        // And a reporter that still has the PR puts it back.
        Assert.Equal(
            RecordPullRequestOutcome.Recorded,
            await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = "pulls/1" }, TestContext.Current.CancellationToken));
        Assert.Equal("pulls/1", Assert.Single((await _svc.GetAsync(wi.Id, TestContext.Current.CancellationToken))!.PullRequests).Url);
    }

    [Fact]
    public async Task RecordPullRequest_tells_a_missing_item_apart_from_a_bad_report()
    {
        var wi = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" }, TestContext.Current.CancellationToken);

        // The two are different answers to the caller: one says stop asking,
        // the other says fix the request. Neither may be reported as the other
        // (the endpoint maps them to 404 and 400).
        Assert.Equal(
            RecordPullRequestOutcome.NotFound,
            await _svc.RecordPullRequestAsync("WI-nope", new RecordPullRequestRequest { Url = "pulls/1" }, TestContext.Current.CancellationToken));
        Assert.Equal(
            RecordPullRequestOutcome.InvalidRequest,
            await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = "  " }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deleting_a_work_item_takes_its_pull_requests_with_it()
    {
        var wi = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a" }, TestContext.Current.CancellationToken);
        await _svc.RecordPullRequestAsync(wi.Id, new RecordPullRequestRequest { Url = "pulls/1" }, TestContext.Current.CancellationToken);

        Assert.True(await _svc.DeleteAsync(wi.Id, TestContext.Current.CancellationToken));

        Assert.Null(await _svc.GetAsync(wi.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListAsync_filters_by_status_and_tags()
    {
        await _svc.CreateAsync(new CreateWorkItemRequest { Title = "a", Tags = new[] { "feature" }, ForceStatus = WorkItemStatus.Ready }, TestContext.Current.CancellationToken);
        await _svc.CreateAsync(new CreateWorkItemRequest { Title = "b", Tags = new[] { "bug-fix" }, ForceStatus = WorkItemStatus.Ready }, TestContext.Current.CancellationToken);
        await _svc.CreateAsync(new CreateWorkItemRequest { Title = "c", Tags = new[] { "feature" }, ForceStatus = WorkItemStatus.Backlog }, TestContext.Current.CancellationToken);

        var list = await _svc.ListAsync(WorkItemStatus.Ready, new[] { "feature" }, TestContext.Current.CancellationToken);

        Assert.Equal("a", Assert.Single(list).Title);
    }

    /// <summary>
    /// A save writes the item before its history: the update takes the item's row
    /// lock, so concurrent writers' history rows are numbered in the order their
    /// changes landed, which is the order old values are paired in.
    /// </summary>
    [Fact]
    public async Task A_save_updates_the_item_before_it_inserts_the_history_of_the_change()
    {
        var wi = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "before" }, TestContext.Current.CancellationToken);
        var saves = new SaveRecorder();
        await using var recorded = new WorkItemServerDbContext(
            new DbContextOptionsBuilder<WorkItemServerDbContext>().UseSqlite(_conn).AddInterceptors(saves).Options)
        {
            HistoryActor = TestActors.ServerHuman,
        };

        await new WorkItemService(recorded, _clock)
            .UpdateAsync(wi.Id, new UpdateWorkItemRequest { Title = "after" }, TestContext.Current.CancellationToken);

        var update = saves.Pending.FindIndex(s => s.Contains((typeof(WorkItem), EntityState.Modified)));
        var history = saves.Pending.FindIndex(s => s.Contains((typeof(WorkItemHistoryEntry), EntityState.Added)));
        Assert.True(update >= 0 && history > update,
            string.Join("\n", saves.Pending.Select(s => string.Join(", ", s))));
    }

    /// <summary>The entity changes each pass of a save writes, in the order the passes run.</summary>
    private sealed class SaveRecorder : SaveChangesInterceptor
    {
        public List<List<(Type Entity, EntityState State)>> Pending { get; } = new();

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Pending.Add(eventData.Context!.ChangeTracker.Entries()
                .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .Select(e => (e.Metadata.ClrType, e.State))
                .ToList());
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CommandTextRecorder : DbCommandInterceptor
    {
        public List<string> Executed { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Executed.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Executed.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
