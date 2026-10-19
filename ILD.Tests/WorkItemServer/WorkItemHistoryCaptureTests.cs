using System.Data.Common;
using System.Reflection;
using ILD.WorkItemServer;
using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Dtos;
using ILD.WorkItemServer.Hosting;
using ILD.WorkItemServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// What the WorkItem server writes to a work item's history, at the service
/// level on SQLite: one save per change, holding only the tracked fields that
/// changed, attributed to the context's actor — including the writes that go
/// around the change tracker (the Running claim, an approved edit proposal, a
/// PR record) and the cascades the server makes on its own.
/// </summary>
public sealed class WorkItemHistoryCaptureTests : IAsyncLifetime
{
    private static readonly string[] TrackedFields =
    {
        "title", "description", "status", "priority", "tags", "dependencies", "repository",
        "aiProviderOverride", "aiProviderOverrideId", "branchNameOverride", "baseBranchOverride",
    };

    private static readonly WorkItemActor Alice = new("alice", WorkItemActorKind.Human, null, null);
    private static readonly WorkItemActor Bob = new("bob", WorkItemActorKind.Human, null, null);

    private readonly FailingHistoryInsert _failHistoryInsert = new();
    private SqliteConnection _conn = null!;
    private DbContextOptions<WorkItemServerDbContext> _options = null!;
    private WorkItemServerDbContext _db = null!;
    private TestClock _clock = null!;
    private WorkItemService _svc = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync();
        _options = new DbContextOptionsBuilder<WorkItemServerDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(_failHistoryInsert)
            .Options;
        _db = new WorkItemServerDbContext(_options);
        await _db.Database.EnsureCreatedAsync();
        _db.HistoryActor = Alice;
        _clock = new TestClock(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        _svc = new WorkItemService(_db, _clock);
    }

    public async ValueTask DisposeAsync()
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

    /// <summary>Fails every insert into the history table while armed.</summary>
    private sealed class FailingHistoryInsert : DbCommandInterceptor
    {
        public bool Armed;

        private void ThrowIfHistoryInsert(DbCommand command)
        {
            if (Armed && command.CommandText.Contains("INSERT INTO \"WorkItemHistoryEntries\"", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("history insert failed");
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            ThrowIfHistoryInsert(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            ThrowIfHistoryInsert(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            ThrowIfHistoryInsert(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            ThrowIfHistoryInsert(command);
            return ValueTask.FromResult(result);
        }
    }

    private async Task<List<WorkItemHistoryEntry>> RowsAsync(string workItemId)
    {
        await using var read = new WorkItemServerDbContext(_options);
        return await read.WorkItemHistoryEntries.AsNoTracking()
            .Where(e => e.WorkItemId == workItemId)
            .OrderBy(e => e.Id)
            .ToListAsync(Ct);
    }

    private async Task<List<WorkItemHistoryEntry>> RowsAfterAsync(string workItemId, int skip)
        => (await RowsAsync(workItemId)).Skip(skip).ToList();

    private async Task<WorkItem> ReadItemAsync(string workItemId)
    {
        await using var read = new WorkItemServerDbContext(_options);
        return await read.WorkItems.AsNoTracking().SingleAsync(w => w.Id == workItemId, Ct);
    }

    private static void AssertOneSave(IReadOnlyList<WorkItemHistoryEntry> rows, WorkItemHistoryKind kind, WorkItemActor actor)
    {
        Assert.NotEmpty(rows);
        Assert.Single(rows.Select(r => r.SaveId).Distinct());
        Assert.All(rows, r =>
        {
            Assert.Equal(kind, r.Kind);
            Assert.Equal(actor.Username, r.Username);
            Assert.Equal(actor.Kind, r.ActorKind);
            Assert.Equal(actor.Detail, r.ActorDetail);
            Assert.Equal(actor.WorkItemId, r.ActorWorkItemId);
        });
    }

    private static string? ValueOf(IEnumerable<WorkItemHistoryEntry> rows, string field)
        => Assert.Single(rows, r => r.Field == field).Value;

    // ── Created ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Creating_an_item_writes_one_created_save_with_every_tracked_field_and_the_creators_actor()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, Ct);
        var repository = Guid.NewGuid();
        var creator = new WorkItemActor("instance-user", WorkItemActorKind.Agent, "AI Coder (run 3f2a)", "7");
        _db.HistoryActor = creator;

        var dto = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "Add a login page",
            Description = "Users sign in with a password.",
            Priority = WorkItemPriority.High,
            ForceStatus = WorkItemStatus.Ready,
            Tags = new[] { "feature", "ui" },
            Dependencies = new[] { dep.Id },
            RepositoryId = repository,
            BranchNameOverride = "feature/login",
        }, Ct);

        var rows = await RowsAsync(dto.Id);
        var stored = await ReadItemAsync(dto.Id);
        Assert.Equal(TrackedFields.Order(StringComparer.Ordinal), rows.Select(r => r.Field).Order(StringComparer.Ordinal));
        AssertOneSave(rows, WorkItemHistoryKind.Created, creator);
        Assert.Equal("Add a login page", ValueOf(rows, "title"));
        Assert.Equal("Users sign in with a password.", ValueOf(rows, "description"));
        Assert.Equal("Ready", ValueOf(rows, "status"));
        Assert.Equal("High", ValueOf(rows, "priority"));
        Assert.Equal(stored.TagsJson, ValueOf(rows, "tags"));
        Assert.Equal(stored.DependenciesJson, ValueOf(rows, "dependencies"));
        Assert.Equal(repository.ToString("D"), ValueOf(rows, "repository"));
        Assert.Equal("None", ValueOf(rows, "aiProviderOverride"));
        Assert.Null(ValueOf(rows, "aiProviderOverrideId"));
        Assert.Equal("feature/login", ValueOf(rows, "branchNameOverride"));
        Assert.Null(ValueOf(rows, "baseBranchOverride"));
    }

    // ── Changed, through the change tracker ────────────────────────────────

    [Fact]
    public async Task An_update_writes_one_changed_save_holding_only_the_fields_whose_value_changed()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "Old title",
            Description = "Same description",
            Tags = new[] { "feature" },
        }, Ct);
        var created = await RowsAsync(dto.Id);
        var provider = Guid.NewGuid();
        _db.HistoryActor = Bob;

        await _svc.UpdateAsync(dto.Id, new UpdateWorkItemRequest
        {
            Title = "New title",
            Description = "Same description",
            Tags = new[] { "feature" },
            AiProviderOverride = AiProviderOverrideMode.OverrideAll,
            AiProviderOverrideId = provider,
        }, Ct);

        var changed = await RowsAfterAsync(dto.Id, created.Count);
        Assert.Equal(
            new[] { "aiProviderOverride", "aiProviderOverrideId", "title" },
            changed.Select(r => r.Field).Order(StringComparer.Ordinal));
        AssertOneSave(changed, WorkItemHistoryKind.Changed, Bob);
        Assert.NotEqual(created[0].SaveId, changed[0].SaveId);
        Assert.Equal("New title", ValueOf(changed, "title"));
        Assert.Equal("OverrideAll", ValueOf(changed, "aiProviderOverride"));
        Assert.Equal(provider.ToString("D"), ValueOf(changed, "aiProviderOverrideId"));

        // Saving the same values again moves UpdatedAt and nothing a person would call a change.
        _clock.Now = _clock.Now.AddMinutes(5);
        await _svc.UpdateAsync(dto.Id, new UpdateWorkItemRequest { Title = "New title", Tags = new[] { "feature" } }, Ct);
        Assert.Equal(created.Count + changed.Count, (await RowsAsync(dto.Id)).Count);
    }

    [Fact]
    public async Task A_second_save_on_the_same_context_does_not_carry_the_first_saves_reason()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, Ct);
        var before = (await RowsAsync(dto.Id)).Count;

        await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.WorkQueue, Reason = "triaged" }, Ct);
        await _svc.UpdateAsync(dto.Id, new UpdateWorkItemRequest { Title = "renamed" }, Ct);

        var rows = await RowsAfterAsync(dto.Id, before);
        Assert.Equal("triaged", Assert.Single(rows, r => r.Field == "status").Reason);
        Assert.Null(Assert.Single(rows, r => r.Field == "title").Reason);
    }

    [Fact]
    public async Task Adding_and_removing_a_dependency_each_record_the_new_dependency_list()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, Ct);
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, Ct);
        var before = (await RowsAsync(dto.Id)).Count;

        Assert.True(await _svc.AddDependencyAsync(dto.Id, dep.Id, Ct));
        var afterAdd = (await ReadItemAsync(dto.Id)).DependenciesJson;
        Assert.True(await _svc.RemoveDependencyAsync(dto.Id, dep.Id, Ct));
        var afterRemove = (await ReadItemAsync(dto.Id)).DependenciesJson;

        var rows = await RowsAfterAsync(dto.Id, before);
        Assert.Equal(new[] { "dependencies", "dependencies" }, rows.Select(r => r.Field));
        Assert.Equal(new[] { afterAdd, afterRemove }, rows.Select(r => r.Value));
        Assert.Equal(2, rows.Select(r => r.SaveId).Distinct().Count());
    }

    [Fact]
    public async Task A_poll_heartbeat_writes_no_history()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x", ForceStatus = WorkItemStatus.Ready }, Ct);
        await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running }, Ct);
        var before = (await RowsAsync(dto.Id)).Count;

        await _svc.PollAsync(new[] { dto.Id }, Ct);
        _clock.Now = _clock.Now.AddMinutes(1);
        await _svc.PollAsync(new[] { dto.Id }, Ct);

        Assert.NotNull((await ReadItemAsync(dto.Id)).LastHeartbeatAt);
        Assert.Equal(before, (await RowsAsync(dto.Id)).Count);
    }

    [Fact]
    public async Task A_tracked_change_with_no_actor_is_refused_and_leaves_neither_a_change_nor_a_row()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "kept" }, Ct);
        var before = (await RowsAsync(dto.Id)).Count;
        _db.HistoryActor = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.UpdateAsync(dto.Id, new UpdateWorkItemRequest { Title = "unattributed" }, Ct));

        Assert.Equal("kept", (await ReadItemAsync(dto.Id)).Title);
        Assert.Equal(before, (await RowsAsync(dto.Id)).Count);
    }

    // ── Transition reasons ─────────────────────────────────────────────────

    public static TheoryData<string, WorkItemStatus, string?, string?, string?> TransitionReasons => new()
    {
        { "a given reason", WorkItemStatus.Done, "Shipped in the release", null, "Shipped in the release" },
        { "no reason", WorkItemStatus.Backlog, null, null, null },
        { "HumanFeedback actions stand in for a missing reason", WorkItemStatus.HumanFeedback, null, "approve,reject", "approve,reject" },
        { "an over-long reason is cut to 2000", WorkItemStatus.Done, new string('r', 2500), null, new string('r', 2000) },
    };

    [Theory]
    [MemberData(nameof(TransitionReasons))]
    public async Task A_transition_stores_its_reason_on_the_status_row(
        string _, WorkItemStatus target, string? reason, string? actions, string? stored)
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x", ForceStatus = WorkItemStatus.WorkQueue }, Ct);
        var before = (await RowsAsync(dto.Id)).Count;

        var resp = await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = target, Reason = reason, Actions = actions }, Ct);

        Assert.True(resp.Success);
        var status = Assert.Single(await RowsAfterAsync(dto.Id, before));
        Assert.Equal("status", status.Field);
        Assert.Equal(target.ToString(), status.Value);
        Assert.Equal(stored, status.Reason);
        Assert.Equal(WorkItemHistoryKind.Changed, status.Kind);
        Assert.Equal("alice", status.Username);
    }

    [Fact]
    public async Task A_blank_reason_never_fails_a_transition()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, Ct);
        var before = (await RowsAsync(dto.Id)).Count;

        var resp = await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.WorkQueue, Reason = "   " }, Ct);

        Assert.True(resp.Success);
        Assert.Equal("WorkQueue", Assert.Single(await RowsAfterAsync(dto.Id, before)).Value);
    }

    // ── The Running claim (ExecuteUpdate) ──────────────────────────────────

    [Fact]
    public async Task A_won_running_claim_writes_a_status_row_and_a_lost_one_writes_none()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x", ForceStatus = WorkItemStatus.Ready }, Ct);
        var before = (await RowsAsync(dto.Id)).Count;
        var runActor = new WorkItemActor("instance-user", WorkItemActorKind.Agent, "run 3f2a", "7");
        _db.HistoryActor = runActor;

        var won = await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running, Reason = "scheduler picked it up" }, Ct);

        Assert.True(won.Success);
        var claim = await RowsAfterAsync(dto.Id, before);
        AssertOneSave(claim, WorkItemHistoryKind.Changed, runActor);
        var status = Assert.Single(claim);
        Assert.Equal("status", status.Field);
        Assert.Equal("Running", status.Value);
        Assert.Equal("scheduler picked it up", status.Reason);

        // A second client that read the item before the claim loses it.
        await using var otherDb = new WorkItemServerDbContext(_options) { HistoryActor = Bob };
        var lost = await new WorkItemService(otherDb, _clock)
            .TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running }, Ct);

        Assert.False(lost.Success);
        Assert.Equal(before + 1, (await RowsAsync(dto.Id)).Count);
    }

    [Fact]
    public async Task A_claim_refused_for_unfinished_dependencies_writes_no_history()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, Ct);
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x", Dependencies = new[] { dep.Id } }, Ct);
        var before = (await RowsAsync(dto.Id)).Count;

        var resp = await _svc.TransitionAsync(dto.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Running }, Ct);

        Assert.False(resp.Success);
        Assert.Equal(before, (await RowsAsync(dto.Id)).Count);
    }

    // ── PR records (ExecuteUpdate) ─────────────────────────────────────────

    [Fact]
    public async Task A_PR_record_writes_an_event_only_when_the_url_is_new_or_it_turns_merged()
    {
        var dto = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "x" }, Ct);
        var before = (await RowsAsync(dto.Id)).Count;
        const string url = "https://forge.example/acme/widgets/pulls/12";
        var day = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        async Task Report(bool merged, DateTime? createdAt = null)
            => Assert.Equal(RecordPullRequestOutcome.Recorded, await _svc.RecordPullRequestAsync(
                dto.Id, new RecordPullRequestRequest { Url = url, Merged = merged, CreatedAt = createdAt ?? day }, Ct));

        await Report(merged: false);
        await Report(merged: false);
        await Report(merged: false, createdAt: day.AddDays(-1));
        await Report(merged: true);
        await Report(merged: true);

        var events = await RowsAfterAsync(dto.Id, before);
        Assert.All(events, e =>
        {
            Assert.Equal("pullRequest", e.Field);
            Assert.Equal(url, e.Value);
            Assert.Equal(WorkItemHistoryKind.Changed, e.Kind);
            Assert.Equal("alice", e.Username);
        });
        Assert.Equal(new[] { "linked", "merged" }, events.Select(e => e.Reason));
        Assert.Equal(2, events.Select(e => e.SaveId).Distinct().Count());
    }

    // ── Approved edit proposals (ExecuteUpdate) ────────────────────────────

    private WorkItemEditProposalService Proposals() => new(_db, _svc, _clock);

    private async Task<(WorkItemDto Item, WorkItemDto Dep)> CreateProposableItemAsync()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, Ct);
        var item = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "Old title",
            Description = "Kept description",
            Tags = new[] { "feature" },
        }, Ct);
        return (item, dep);
    }

    private async Task<Guid> ProposeAsync(string itemId, CreateEditProposalRequest req)
    {
        var created = await Proposals().CreateAsync(itemId, req, Ct);
        Assert.Equal(EditProposalCreateOutcome.Created, created.Outcome);
        return created.Proposal!.Id;
    }

    [Fact]
    public async Task An_approved_proposal_writes_one_changed_save_for_exactly_the_fields_it_changed()
    {
        var (item, dep) = await CreateProposableItemAsync();
        var proposalId = await ProposeAsync(item.Id, new CreateEditProposalRequest
        {
            Title = "New title",
            Description = "Kept description",
            Tags = new[] { "feature", "ui" },
            AddDependencies = new[] { dep.Id },
            CreatedByLoopRunId = Guid.NewGuid(),
        });
        var before = (await RowsAsync(item.Id)).Count;
        var approver = new WorkItemActor("bob", WorkItemActorKind.Human, $"edit proposal {proposalId} by run 3f2a", "7");
        _db.HistoryActor = approver;

        var result = await Proposals().ApproveAsync(item.Id, proposalId, Ct);

        Assert.Equal(EditProposalDecisionOutcome.Applied, result.Outcome);
        var rows = await RowsAfterAsync(item.Id, before);
        Assert.Equal(new[] { "dependencies", "tags", "title" }, rows.Select(r => r.Field).Order(StringComparer.Ordinal));
        AssertOneSave(rows, WorkItemHistoryKind.Changed, approver);
        var stored = await ReadItemAsync(item.Id);
        Assert.Equal("New title", ValueOf(rows, "title"));
        Assert.Equal(stored.TagsJson, ValueOf(rows, "tags"));
        Assert.Equal(stored.DependenciesJson, ValueOf(rows, "dependencies"));
    }

    [Fact]
    public async Task A_stale_or_already_decided_approve_writes_no_history()
    {
        var (item, _) = await CreateProposableItemAsync();
        var staleId = await ProposeAsync(item.Id, new CreateEditProposalRequest { Title = "Proposed title" });
        await _svc.UpdateAsync(item.Id, new UpdateWorkItemRequest { Description = "A human edit since" }, Ct);
        var rejectedId = await ProposeAsync(item.Id, new CreateEditProposalRequest { Title = "Another title" });
        await Proposals().RejectAsync(item.Id, rejectedId, "no", Ct);
        var before = (await RowsAsync(item.Id)).Count;

        Assert.Equal(EditProposalDecisionOutcome.Stale, (await Proposals().ApproveAsync(item.Id, staleId, Ct)).Outcome);
        Assert.Equal(EditProposalDecisionOutcome.NotPending, (await Proposals().ApproveAsync(item.Id, rejectedId, Ct)).Outcome);

        Assert.Equal(before, (await RowsAsync(item.Id)).Count);
    }

    [Fact]
    public async Task An_approve_whose_history_insert_fails_leaves_the_item_unchanged()
    {
        var (item, _) = await CreateProposableItemAsync();
        var proposalId = await ProposeAsync(item.Id, new CreateEditProposalRequest { Title = "New title" });
        var before = (await RowsAsync(item.Id)).Count;

        _failHistoryInsert.Armed = true;
        await Record.ExceptionAsync(() => Proposals().ApproveAsync(item.Id, proposalId, Ct));
        _failHistoryInsert.Armed = false;

        Assert.Equal("Old title", (await ReadItemAsync(item.Id)).Title);
        Assert.Equal(before, (await RowsAsync(item.Id)).Count);
    }

    // ── Cascades the server makes itself ───────────────────────────────────

    [Fact]
    public async Task Promoting_a_dependent_when_its_dependency_is_done_is_recorded_with_the_requests_actor_and_a_reason()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, Ct);
        var dependent = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "dependent",
            ForceStatus = WorkItemStatus.WorkQueue,
            Dependencies = new[] { dep.Id },
        }, Ct);
        var before = (await RowsAsync(dependent.Id)).Count;
        _db.HistoryActor = Bob;

        await _svc.TransitionAsync(dep.Id, new TransitionRequest { TargetStatus = WorkItemStatus.Done, Reason = "merged" }, Ct);

        var promotion = Assert.Single(await RowsAfterAsync(dependent.Id, before));
        Assert.Equal("status", promotion.Field);
        Assert.Equal("Ready", promotion.Value);
        Assert.Equal("bob", promotion.Username);
        Assert.Equal(WorkItemActorKind.Human, promotion.ActorKind);
        Assert.False(string.IsNullOrWhiteSpace(promotion.Reason));
    }

    [Fact]
    public async Task Deleting_an_item_records_the_stripped_dependency_and_the_promotion_and_keeps_its_own_history()
    {
        var dep = await _svc.CreateAsync(new CreateWorkItemRequest { Title = "dep" }, Ct);
        var dependent = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "dependent",
            ForceStatus = WorkItemStatus.WorkQueue,
            Dependencies = new[] { dep.Id },
        }, Ct);
        var depHistory = (await RowsAsync(dep.Id)).Count;
        var before = (await RowsAsync(dependent.Id)).Count;
        _db.HistoryActor = Bob;

        Assert.True(await _svc.DeleteAsync(dep.Id, Ct));

        var cascade = await RowsAfterAsync(dependent.Id, before);
        Assert.Equal("[]", Assert.Single(cascade, r => r.Field == "dependencies").Value);
        Assert.Equal("Ready", Assert.Single(cascade, r => r.Field == "status").Value);
        Assert.All(cascade, r =>
        {
            Assert.Equal("bob", r.Username);
            Assert.False(string.IsNullOrWhiteSpace(r.Reason));
        });
        Assert.Equal(depHistory, (await RowsAsync(dep.Id)).Count);
    }

    public static TheoryData<string, string> BackgroundServices => new()
    {
        { nameof(StaleWorkItemReclaimer), "system: stale reclaim" },
        { nameof(WorkQueueReconciler), "system: work queue reconcile" },
    };

    /// <summary>
    /// One pass of the hosted service, against this test's database. The pass's
    /// IWorkItemService call is forwarded to the real service and cancels the
    /// service's stopping token once it returns, so the loop ends after exactly
    /// one pass without waiting on its interval.
    /// </summary>
    [Theory]
    [MemberData(nameof(BackgroundServices))]
    public async Task The_background_services_record_their_changes_as_named_system_actors(string service, string detail)
    {
        var item = await _svc.CreateAsync(new CreateWorkItemRequest
        {
            Title = "x",
            ForceStatus = service == nameof(StaleWorkItemReclaimer) ? WorkItemStatus.Running : WorkItemStatus.WorkQueue,
        }, Ct);
        var row = await _db.WorkItems.SingleAsync(w => w.Id == item.Id, Ct);
        row.LastHeartbeatAt = _clock.Now.AddHours(-2);
        _db.Entry(row).Property(w => w.LastHeartbeatAt).IsModified = true;
        await _db.SaveChangesAsync(Ct);
        var before = (await RowsAsync(item.Id)).Count;

        using var stopping = new CancellationTokenSource();
        var services = new ServiceCollection();
        services.AddDbContext<WorkItemServerDbContext>(o => o.UseSqlite(_conn));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddScoped<IWorkItemService>(sp => OnePass.Wrap(
            new WorkItemService(sp.GetRequiredService<WorkItemServerDbContext>(), _clock), stopping));
        await using var provider = services.BuildServiceProvider();
        var config = new ConfigurationBuilder().Build();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        BackgroundService hosted = service == nameof(StaleWorkItemReclaimer)
            ? new StaleWorkItemReclaimer(scopes, config, NullLogger<StaleWorkItemReclaimer>.Instance)
            : new WorkQueueReconciler(scopes, config, NullLogger<WorkQueueReconciler>.Instance);

        await hosted.StartAsync(stopping.Token);
        await hosted.ExecuteTask!;

        var status = Assert.Single(await RowsAfterAsync(item.Id, before));
        Assert.Equal("status", status.Field);
        Assert.Equal("Ready", status.Value);
        Assert.Equal("system", status.Username);
        Assert.Equal(WorkItemActorKind.System, status.ActorKind);
        Assert.Equal(detail, status.ActorDetail);
    }

    /// <summary>Forwards every call; ends the hosted loop once a background pass returns.</summary>
    public class OnePass : DispatchProxy
    {
        private IWorkItemService _inner = null!;
        private CancellationTokenSource _stop = null!;

        public static IWorkItemService Wrap(IWorkItemService inner, CancellationTokenSource stop)
        {
            var proxy = Create<IWorkItemService, OnePass>();
            var self = (OnePass)(object)proxy;
            self._inner = inner;
            self._stop = stop;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod!.Invoke(_inner, args);
            if (targetMethod.Name is not (nameof(IWorkItemService.ReclaimStaleAsync) or nameof(IWorkItemService.ReconcileWorkQueueAsync)))
                return result;
            var pass = (Task<int>)result!;
            return pass.ContinueWith(t =>
            {
                _stop.Cancel();
                return t;
            }, TaskScheduler.Default).Unwrap();
        }
    }
}
