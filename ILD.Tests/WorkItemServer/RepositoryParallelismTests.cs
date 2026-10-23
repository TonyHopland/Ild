using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// A repository's parallelism limit, enforced where the scheduler claims Ready
/// items. Passes run against the real WorkItem Server semantics (the fake
/// harness), so board columns, transitions and history are the server's own.
/// </summary>
public sealed class RepositoryParallelismTests : IDisposable
{
    private static readonly WorkItemServerOptions Opts = new() { BaseUrl = "http://x", ApiKey = "k" };
    private const string NoLoopTag = "no-loop";

    private readonly TestDb _db = new();
    private readonly Mock<ILoopEngine> _engine = new();
    private readonly Mock<IWorkItemNotifier> _notifier = new();
    private readonly RepositoryParallelismHolds _holds = new();
    private readonly Guid _remoteProviderId;
    private readonly RemoteWorkItemCoordinator _sut;

    public RepositoryParallelismTests()
    {
        var remote = new RemoteProvider { Id = Guid.NewGuid(), Name = "r", Type = "Forgejo", Url = "https://example.com" };
        _db.Context.RemoteProviders.Add(remote);
        _db.Context.SaveChanges();
        _remoteProviderId = remote.Id;

        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns((IReadOnlyList<string> tags) => tags.Contains(NoLoopTag)
                    ? new LoopTemplateResolution(LoopTemplateResolutionKind.None, null, Array.Empty<string>())
                    : new LoopTemplateResolution(LoopTemplateResolutionKind.Single, Guid.NewGuid(), Array.Empty<string>()));

        _sut = new RemoteWorkItemCoordinator(_db.ServerClient, resolver.Object, _engine.Object, _db.LoopRuns,
            TestActors.Instance, providerStore: _db.Providers, workItemNotifier: _notifier.Object, repositoryHolds: _holds);
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid AddRepo(int parallelism)
    {
        var repo = new Repository
        {
            Id = Guid.NewGuid(), Name = "repo", RemoteProviderId = _remoteProviderId,
            CloneUrl = "https://example.com/repo.git", Parallelism = parallelism,
        };
        _db.Context.Repositories.Add(repo);
        _db.Context.SaveChanges();
        return repo.Id;
    }

    private void SetLimit(Guid repoId, int parallelism)
    {
        _db.Context.Repositories.Single(r => r.Id == repoId).Parallelism = parallelism;
        _db.Context.SaveChanges();
    }

    private async Task<string> AddItemAsync(Guid? repoId, RemoteWorkItemStatus status, string title = "w",
        string? branchNameOverride = null, string? baseBranchOverride = null, params string[] tags)
    {
        var created = await _db.ServerClient.CreateAsync(Opts, new RemoteCreateWorkItemRequest
        {
            Title = title,
            Tags = tags.Length == 0 ? new[] { "build" } : tags,
            ForceStatus = status,
            RepositoryId = repoId,
            BranchNameOverride = branchNameOverride,
            BaseBranchOverride = baseBranchOverride,
        }, TestActors.Human, Ct);
        return created.Id;
    }

    private async Task MoveAsync(string id, RemoteWorkItemStatus status)
    {
        var resp = await _db.ServerClient.TransitionAsync(Opts, id,
            new RemoteTransitionRequest { TargetStatus = status, Reason = "test" }, TestActors.Human, Ct);
        Assert.True(resp.Success);
    }

    private async Task<RemoteWorkItemStatus> StatusOfAsync(string id)
        => (await _db.ServerClient.GetAsync(Opts, id, Ct))!.Status;

    private async Task<int> HistoryCountAsync(string id)
        => (await _db.ServerClient.GetHistoryAsync(Opts, id, Ct))!.Count;

    private async Task<IReadOnlyList<string>> ServerReadyOrderAsync()
        => (await _db.ServerClient.PollAsync(Opts, Array.Empty<string>(), WorkItemActor.System(TestActors.Instance.Username, "scheduler"), Ct)).ReadyItems.Select(w => w.Id).ToList();

    private Task<PollCycleResult> PassAsync(int maxConcurrent = 10, bool claimReadyItems = true)
        => _sut.RunPollCycleAsync(Opts, maxConcurrent, claimReadyItems, Ct);

    private static IReadOnlyList<string> Ids(IEnumerable<RemoteWorkItem> items) => items.Select(w => w.Id).ToList();

    private WorkItemManager Manager() => new(new Mock<IRepositoryManager>().Object, _db.Providers,
        new Mock<IEventLogService>().Object, _db.LoopRuns, _db.ServerClient, _db.ServerOptions, TestActors.Instance,
        repositoryHolds: _holds);

    [Fact]
    public async Task With_parallelism_one_items_run_one_after_another_and_the_rest_stay_in_ready_untouched()
    {
        var repo = AddRepo(parallelism: 1);
        var a = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "A");
        var b = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "B");
        var c = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "C");
        var order = await ServerReadyOrderAsync();
        var historyBefore = new Dictionary<string, int>();
        foreach (var id in new[] { a, b, c }) historyBefore[id] = await HistoryCountAsync(id);

        var first = await PassAsync();

        Assert.Equal(new[] { order[0] }, Ids(first.Claimed));
        Assert.Empty(first.EscalatedToHumanFeedback);
        Assert.False(first.BlockedByCap);
        var waiting = order.Skip(1).ToList();
        Assert.Equal(waiting.OrderBy(x => x), Ids(first.HeldByRepositoryLimit).OrderBy(x => x));
        foreach (var id in waiting)
        {
            Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(id));
            Assert.Equal(historyBefore[id], await HistoryCountAsync(id));
            Assert.Null(await _db.LoopRuns.GetWorkItemStatusReasonAsync(id));
            _engine.Verify(e => e.StartRunAsync(id, It.IsAny<CancellationToken>()), Times.Never);
        }

        // Every column that holds a slot keeps the next item waiting.
        foreach (var holding in new[] { RemoteWorkItemStatus.Running, RemoteWorkItemStatus.HumanFeedback, RemoteWorkItemStatus.WaitingForIld })
        {
            if (await StatusOfAsync(order[0]) != holding) await MoveAsync(order[0], holding);
            var stalled = await PassAsync();
            Assert.Empty(stalled.Claimed);
            foreach (var id in waiting) Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(id));
        }

        await MoveAsync(order[0], RemoteWorkItemStatus.Done);
        var second = await PassAsync();
        Assert.Equal(new[] { waiting[0] }, Ids(second.Claimed));
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(waiting[1]));

        Assert.Empty((await PassAsync()).Claimed);

        await MoveAsync(waiting[0], RemoteWorkItemStatus.Done);
        var third = await PassAsync();
        Assert.Equal(new[] { waiting[1] }, Ids(third.Claimed));
        Assert.Empty(third.HeldByRepositoryLimit);
    }

    private async Task<double> PriorityOfAsync(string id)
        => (await _db.ServerClient.GetAsync(Opts, id, Ct))!.Priority;

    [Fact]
    public async Task Three_ready_items_of_a_one_at_a_time_repository_are_claimed_in_board_order_and_a_reorder_picks_the_next()
    {
        var repo = AddRepo(parallelism: 1);
        var a = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "A");
        var b = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "B");
        var c = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "C");
        var manager = Manager();
        await manager.ReorderAsync(TestActors.Human, a, aboveId: null, belowId: c);
        await manager.ReorderAsync(TestActors.Human, b, aboveId: a, belowId: c);
        Assert.Equal(new[] { a, b, c }, await ServerReadyOrderAsync());

        Assert.Equal(new[] { a }, Ids((await PassAsync()).Claimed));
        await MoveAsync(a, RemoteWorkItemStatus.Done);

        await manager.ReorderAsync(TestActors.Human, c, aboveId: null, belowId: b);
        Assert.Equal(new[] { c }, Ids((await PassAsync()).Claimed));
        await MoveAsync(c, RemoteWorkItemStatus.Done);

        Assert.Equal(new[] { b }, Ids((await PassAsync()).Claimed));
    }

    /// <summary>
    /// The top Ready item waits for its repository, the next eligible item below
    /// starts instead, and the held item keeps its place: once the slot frees up
    /// it starts ahead of the item that was below it.
    /// </summary>
    [Fact]
    public async Task A_top_item_held_by_its_repository_is_skipped_untouched_and_starts_first_once_it_can()
    {
        var repo = AddRepo(parallelism: 1);
        var busy = await AddItemAsync(repo, RemoteWorkItemStatus.Running, "busy");
        var free = await AddItemAsync(null, RemoteWorkItemStatus.Ready, "free");
        var below = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "below");
        var top = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "top");
        Assert.Equal(new[] { top, below, free }, await ServerReadyOrderAsync());
        var priority = await PriorityOfAsync(top);
        var history = await HistoryCountAsync(top);

        Assert.Equal(new[] { free }, Ids((await PassAsync()).Claimed));
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(top));
        Assert.Equal(priority, await PriorityOfAsync(top));
        Assert.Equal(history, await HistoryCountAsync(top));

        await MoveAsync(busy, RemoteWorkItemStatus.Done);
        Assert.Equal(new[] { top }, Ids((await PassAsync()).Claimed));
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(below));
    }

    [Fact]
    public async Task A_top_item_with_an_unfinished_dependency_is_skipped_untouched_and_starts_first_once_it_can()
    {
        var repo = AddRepo(parallelism: 1);
        var dependency = await AddItemAsync(null, RemoteWorkItemStatus.Backlog, "dependency");
        var last = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "last");
        var next = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "next");
        var top = (await _db.ServerClient.CreateAsync(Opts, new RemoteCreateWorkItemRequest
        {
            Title = "top",
            Tags = new[] { "build" },
            ForceStatus = RemoteWorkItemStatus.Ready,
            RepositoryId = repo,
            Dependencies = new[] { dependency },
        }, TestActors.Human, Ct)).Id;
        Assert.Equal(new[] { top, next, last }, await ServerReadyOrderAsync());
        var priority = await PriorityOfAsync(top);
        var history = await HistoryCountAsync(top);

        Assert.Equal(new[] { next }, Ids((await PassAsync()).Claimed));
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(top));
        Assert.Equal(priority, await PriorityOfAsync(top));
        Assert.Equal(history, await HistoryCountAsync(top));

        await MoveAsync(next, RemoteWorkItemStatus.Done);
        await MoveAsync(dependency, RemoteWorkItemStatus.Done);
        Assert.Equal(new[] { top }, Ids((await PassAsync()).Claimed));
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(last));
    }

    /// <summary>
    /// The board's Work queue column holds Work queue and Ready items in one
    /// order. That order is a preference, not a line: the top-most item that can
    /// start starts, and the waiting items above it keep their place.
    /// </summary>
    [Fact]
    public async Task The_top_most_item_of_the_work_queue_column_that_can_start_starts_and_the_waiting_ones_above_it_stay()
    {
        var repo = AddRepo(parallelism: 1);
        await AddItemAsync(repo, RemoteWorkItemStatus.Running, "busy");
        var dependency = await AddItemAsync(null, RemoteWorkItemStatus.Backlog, "dependency");
        var queued = (await _db.ServerClient.CreateAsync(Opts, new RemoteCreateWorkItemRequest
        {
            Title = "queued",
            Tags = new[] { "build" },
            ForceStatus = RemoteWorkItemStatus.WorkQueue,
            Dependencies = new[] { dependency },
        }, TestActors.Human, Ct)).Id;
        var held = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "held");
        var startable = await AddItemAsync(null, RemoteWorkItemStatus.Ready, "startable");
        var manager = Manager();
        await manager.ReorderAsync(TestActors.Human, queued, aboveId: null, belowId: startable);
        await manager.ReorderAsync(TestActors.Human, held, aboveId: queued, belowId: startable);
        var column = new WorkItemListQuery
        {
            Statuses = [RemoteWorkItemStatus.WorkQueue, RemoteWorkItemStatus.Ready],
            OrderBy = WorkItemOrderBy.Priority,
        };
        Assert.Equal(new[] { queued, held, startable }, (await manager.ListPageAsync(column)).Items.Select(v => v.Id));
        Assert.Equal(RemoteWorkItemStatus.WorkQueue, await StatusOfAsync(queued));
        var priorities = new Dictionary<string, double>
        {
            [queued] = await PriorityOfAsync(queued),
            [held] = await PriorityOfAsync(held),
        };

        Assert.Equal(new[] { startable }, Ids((await PassAsync()).Claimed));

        Assert.Equal(RemoteWorkItemStatus.WorkQueue, await StatusOfAsync(queued));
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(held));
        foreach (var (id, priority) in priorities) Assert.Equal(priority, await PriorityOfAsync(id));
        Assert.Equal(new[] { queued, held }, (await manager.ListPageAsync(column)).Items.Select(v => v.Id));
    }

    [Theory]
    [InlineData(RemoteWorkItemStatus.Running, true)]
    [InlineData(RemoteWorkItemStatus.HumanFeedback, true)]
    [InlineData(RemoteWorkItemStatus.WaitingForIld, true)]
    [InlineData(RemoteWorkItemStatus.Done, false)]
    [InlineData(RemoteWorkItemStatus.Backlog, false)]
    [InlineData(RemoteWorkItemStatus.WorkQueue, false)]
    public async Task Only_running_needs_you_and_waiting_for_ild_items_take_the_repository_slot(RemoteWorkItemStatus status, bool holdsSlot)
    {
        var repo = AddRepo(parallelism: 1);
        await AddItemAsync(repo, status, "other");
        var ready = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "next");

        var result = await PassAsync();

        if (holdsSlot)
        {
            Assert.Empty(result.Claimed);
            Assert.Equal(new[] { ready }, Ids(result.HeldByRepositoryLimit));
            Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(ready));
        }
        else
        {
            Assert.Equal(new[] { ready }, Ids(result.Claimed));
            Assert.Empty(result.HeldByRepositoryLimit);
        }
    }

    [Theory]
    [InlineData("custom-branch", null)]
    [InlineData(null, "release")]
    public async Task An_in_progress_item_with_a_branch_override_still_takes_the_slot(string? branchName, string? baseBranch)
    {
        var repo = AddRepo(parallelism: 1);
        await AddItemAsync(repo, RemoteWorkItemStatus.Running, "overridden", branchName, baseBranch);
        var ready = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "next");

        var result = await PassAsync();

        Assert.Empty(result.Claimed);
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(ready));
    }

    [Fact]
    public async Task A_ready_item_escalated_for_its_template_takes_the_slot_for_the_rest_of_the_pass()
    {
        var repo = AddRepo(parallelism: 1);
        await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "x", tags: "tag-x");
        await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "y", tags: "tag-y");
        var order = await ServerReadyOrderAsync();
        // Whichever the server hands over first is the one that fails to resolve.
        var firstTags = (await _db.ServerClient.GetAsync(Opts, order[0], Ct))!.Tags.ToArray();
        await _db.ServerClient.UpdateAsync(Opts, order[0],
            new RemoteUpdateWorkItemRequest { Tags = firstTags.Append(NoLoopTag).ToList() }, TestActors.Human, Ct);

        var result = await PassAsync();

        Assert.Equal(new[] { order[0] }, Ids(result.EscalatedToHumanFeedback));
        Assert.Empty(result.Claimed);
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(order[1]));
        Assert.Equal(new[] { order[1] }, Ids(result.HeldByRepositoryLimit));
    }

    [Fact]
    public async Task An_item_whose_run_fails_to_start_keeps_the_slot_in_needs_you()
    {
        var repo = AddRepo(parallelism: 1);
        await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "A");
        await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "B");
        await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "C");
        var order = await ServerReadyOrderAsync();
        _engine.Setup(e => e.StartRunAsync(order[0], It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("boom"));

        await PassAsync();

        Assert.Equal(RemoteWorkItemStatus.HumanFeedback, await StatusOfAsync(order[0]));
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(order[1]));
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(order[2]));
        _engine.Verify(e => e.StartRunAsync(It.Is<string>(id => id != order[0]), It.IsAny<CancellationToken>()), Times.Never);

        Assert.Empty((await PassAsync()).Claimed);
    }

    [Fact]
    public async Task Claims_within_one_pass_count_immediately_so_the_limit_is_never_exceeded()
    {
        var repo = AddRepo(parallelism: 2);
        var ids = new List<string>();
        for (var i = 0; i < 5; i++) ids.Add(await AddItemAsync(repo, RemoteWorkItemStatus.Ready, $"item {i}"));

        var result = await PassAsync();

        Assert.Equal(2, result.Claimed.Count);
        Assert.Equal(3, result.HeldByRepositoryLimit.Count);
        var running = 0;
        foreach (var id in ids)
            if (await StatusOfAsync(id) == RemoteWorkItemStatus.Running) running++;
        Assert.Equal(2, running);
    }

    [Fact]
    public async Task A_limit_on_one_repository_does_not_hold_back_another_or_items_without_a_repository()
    {
        var full = AddRepo(parallelism: 1);
        var other = AddRepo(parallelism: 1);
        await AddItemAsync(full, RemoteWorkItemStatus.Running, "busy");
        var fullReady1 = await AddItemAsync(full, RemoteWorkItemStatus.Ready, "full 1");
        var fullReady2 = await AddItemAsync(full, RemoteWorkItemStatus.Ready, "full 2");
        var otherReady1 = await AddItemAsync(other, RemoteWorkItemStatus.Ready, "other 1");
        var otherReady2 = await AddItemAsync(other, RemoteWorkItemStatus.Ready, "other 2");
        var noRepo = await AddItemAsync(null, RemoteWorkItemStatus.Ready, "no repository");

        var result = await PassAsync();

        var claimed = Ids(result.Claimed);
        Assert.Contains(noRepo, claimed);
        Assert.Single(claimed, id => id == otherReady1 || id == otherReady2);
        Assert.DoesNotContain(fullReady1, claimed);
        Assert.DoesNotContain(fullReady2, claimed);
        Assert.Equal(2, claimed.Count);
        Assert.False(result.BlockedByCap);
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(fullReady1));
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(fullReady2));
    }

    [Fact]
    public async Task Repository_held_items_do_not_report_the_global_cap_but_unheld_ones_still_do()
    {
        var repo = AddRepo(parallelism: 1);
        var busy = await AddItemAsync(repo, RemoteWorkItemStatus.Running, "busy");
        var held = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "held");
        // A live local run, so the busy item also fills the instance's only global slot.
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        _db.Context.LoopTemplates.Add(template);
        _db.Context.LoopTemplateVersions.Add(version);
        _db.Context.LoopRuns.Add(new LoopRun
        {
            Id = Guid.NewGuid(), WorkItemId = busy, LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running, StartedAt = DateTime.UtcNow,
        });
        _db.Context.SaveChanges();

        var onlyHeld = await PassAsync(maxConcurrent: 1);
        Assert.False(onlyHeld.BlockedByCap);
        Assert.Equal(new[] { held }, Ids(onlyHeld.HeldByRepositoryLimit));

        var free = await AddItemAsync(null, RemoteWorkItemStatus.Ready, "free");

        var capped = await PassAsync(maxConcurrent: 1);
        Assert.Empty(capped.Claimed);
        Assert.True(capped.BlockedByCap);
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(free));
    }

    [Fact]
    public async Task Lowering_the_limit_stops_nothing_in_progress_and_only_holds_new_claims()
    {
        var repo = AddRepo(parallelism: 0);
        var a = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "A");
        var b = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "B");
        Assert.Equal(2, (await PassAsync()).Claimed.Count);

        var c = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "C");
        SetLimit(repo, 1);
        var lowered = await PassAsync();

        Assert.Empty(lowered.Claimed);
        Assert.Equal(RemoteWorkItemStatus.Running, await StatusOfAsync(a));
        Assert.Equal(RemoteWorkItemStatus.Running, await StatusOfAsync(b));
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(c));
        _engine.Verify(e => e.StopRunAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);

        // One finishing still leaves the count at the new limit.
        await MoveAsync(a, RemoteWorkItemStatus.Done);
        Assert.Empty((await PassAsync()).Claimed);

        await MoveAsync(b, RemoteWorkItemStatus.Done);
        Assert.Equal(new[] { c }, Ids((await PassAsync()).Claimed));
    }

    [Fact]
    public async Task The_board_shows_the_hold_for_a_held_ready_item_and_nothing_for_any_other()
    {
        var repo = AddRepo(parallelism: 1);
        var busy = await AddItemAsync(repo, RemoteWorkItemStatus.Running, "busy");
        var held = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "held");
        var free = await AddItemAsync(null, RemoteWorkItemStatus.Ready, "free");
        var mgr = Manager();

        Assert.Null((await mgr.GetWorkItemAsync(held))!.RepositoryParallelismHold);

        // Held while paused too: the hold is a fact about the slots, not about claiming.
        await PassAsync(claimReadyItems: false);

        Assert.Equal(new RepositoryParallelismHold(1, 1), (await mgr.GetWorkItemAsync(held))!.RepositoryParallelismHold);
        Assert.Null((await mgr.GetWorkItemAsync(busy))!.RepositoryParallelismHold);
        Assert.Null((await mgr.GetWorkItemAsync(free))!.RepositoryParallelismHold);

        var page = await mgr.ListPageAsync(new WorkItemListQuery { Statuses = [RemoteWorkItemStatus.Ready] });
        Assert.Equal(new RepositoryParallelismHold(1, 1), page.Items.Single(v => v.Id == held).RepositoryParallelismHold);
        Assert.Null(page.Items.Single(v => v.Id == free).RepositoryParallelismHold);

        // Moved out of Ready by a person before the next pass: no longer waiting on the limit.
        await MoveAsync(held, RemoteWorkItemStatus.Backlog);
        Assert.Null((await mgr.GetWorkItemAsync(held))!.RepositoryParallelismHold);
    }

    [Fact]
    public async Task The_hold_reports_the_counts_of_the_latest_pass_and_disappears_once_claimed()
    {
        var repo = AddRepo(parallelism: 2);
        var a = await AddItemAsync(repo, RemoteWorkItemStatus.Running, "A");
        var b = await AddItemAsync(repo, RemoteWorkItemStatus.HumanFeedback, "B");
        var c = await AddItemAsync(repo, RemoteWorkItemStatus.WaitingForIld, "C");
        var next = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "next");
        var mgr = Manager();

        await PassAsync();
        Assert.Equal(new RepositoryParallelismHold(3, 2), (await mgr.GetWorkItemAsync(next))!.RepositoryParallelismHold);

        await MoveAsync(a, RemoteWorkItemStatus.Done);
        await PassAsync();
        Assert.Equal(new RepositoryParallelismHold(2, 2), (await mgr.GetWorkItemAsync(next))!.RepositoryParallelismHold);

        await MoveAsync(b, RemoteWorkItemStatus.Done);
        var claimed = await PassAsync();
        Assert.Equal(new[] { next }, Ids(claimed.Claimed));
        Assert.Null((await mgr.GetWorkItemAsync(next))!.RepositoryParallelismHold);
        Assert.Equal(RemoteWorkItemStatus.WaitingForIld, await StatusOfAsync(c));
    }

    [Fact]
    public async Task An_open_board_is_told_when_a_hold_appears_or_changes_and_not_when_it_stays_the_same()
    {
        var repo = AddRepo(parallelism: 1);
        var a = await AddItemAsync(repo, RemoteWorkItemStatus.Running, "A");
        var b = await AddItemAsync(repo, RemoteWorkItemStatus.HumanFeedback, "B");
        var held = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "held");
        void Told(Times times) => _notifier.Verify(n => n.WorkItemStateChangedAsync(held,
            RemoteWorkItemStatus.Ready, RemoteWorkItemStatus.Ready), times);

        await PassAsync();
        Told(Times.Once());

        await PassAsync();
        Told(Times.Once());

        await MoveAsync(a, RemoteWorkItemStatus.Done);
        await PassAsync();
        Told(Times.Exactly(2));

        await MoveAsync(b, RemoteWorkItemStatus.Done);
        await PassAsync();
        Told(Times.Exactly(2));
        _notifier.Verify(n => n.WorkItemStateChangedAsync(held,
            RemoteWorkItemStatus.Ready, RemoteWorkItemStatus.Running), Times.Once);
    }

    [Fact]
    public async Task Raising_the_limit_clears_the_hold_and_tells_the_board_even_while_paused()
    {
        var repo = AddRepo(parallelism: 1);
        await AddItemAsync(repo, RemoteWorkItemStatus.Running, "busy");
        var held = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "held");
        var mgr = Manager();

        await PassAsync(claimReadyItems: false);
        Assert.NotNull((await mgr.GetWorkItemAsync(held))!.RepositoryParallelismHold);

        SetLimit(repo, 0);
        await PassAsync(claimReadyItems: false);

        Assert.Null((await mgr.GetWorkItemAsync(held))!.RepositoryParallelismHold);
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(held));
        _notifier.Verify(n => n.WorkItemStateChangedAsync(held,
            RemoteWorkItemStatus.Ready, RemoteWorkItemStatus.Ready), Times.Exactly(2));
    }

    // ── Start anyway: a human starts one Ready item past its repository's limit ──

    private Task<HandStartResult> StartAnywayAsync(string id, int maxConcurrent = 10)
        => _sut.StartIgnoringRepositoryLimitAsync(Opts, id, maxConcurrent, TestActors.Human, Ct);

    private void SeedLiveRun(string workItemId)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t-" + workItemId };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        _db.Context.LoopTemplates.Add(template);
        _db.Context.LoopTemplateVersions.Add(version);
        _db.Context.LoopRuns.Add(new LoopRun
        {
            Id = Guid.NewGuid(), WorkItemId = workItemId, LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running, StartedAt = DateTime.UtcNow,
        });
        _db.Context.SaveChanges();
    }

    private async Task AssertUntouchedAsync(string id, int historyBefore)
    {
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(id));
        Assert.Equal(historyBefore, await HistoryCountAsync(id));
        Assert.Null(await _db.LoopRuns.GetWorkItemStatusReasonAsync(id));
        _engine.Verify(e => e.StartRunAsync(id, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Start_anyway_runs_one_held_item_leaves_the_limit_as_it_is_and_the_started_item_takes_a_slot()
    {
        var repo = AddRepo(parallelism: 1);
        var a = await AddItemAsync(repo, RemoteWorkItemStatus.Running, "A");
        var b = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "B");
        var c = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "C");
        var mgr = Manager();
        var held = await PassAsync();
        Assert.Equal(new[] { b, c }.OrderBy(x => x), Ids(held.HeldByRepositoryLimit).OrderBy(x => x));
        var repoBefore = _db.Fresh().Repositories.AsNoTracking().Single(r => r.Id == repo);
        var cHistory = await HistoryCountAsync(c);

        var result = await StartAnywayAsync(b);

        Assert.Equal(HandStartResult.Started, result);
        Assert.Equal(RemoteWorkItemStatus.Running, await StatusOfAsync(b));
        _engine.Verify(e => e.StartRunAsync(b, It.IsAny<CancellationToken>()), Times.Once);
        _notifier.Verify(n => n.WorkItemStateChangedAsync(b, RemoteWorkItemStatus.Ready, RemoteWorkItemStatus.Running), Times.Once);
        var claim = (await _db.ServerClient.GetHistoryAsync(Opts, b, Ct))!
            .Last(e => e.Field == "status" && e.NewValue == nameof(RemoteWorkItemStatus.Running));
        Assert.Equal(TestActors.Human.Username, claim.Username);
        await AssertUntouchedAsync(c, cHistory);

        var repoAfter = _db.Fresh().Repositories.AsNoTracking().Single(r => r.Id == repo);
        Assert.Equal(1, repoAfter.Parallelism);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(repoBefore), System.Text.Json.JsonSerializer.Serialize(repoAfter));

        // B now holds a slot like any other in-progress item.
        Assert.Empty((await PassAsync()).Claimed);
        Assert.Equal(new RepositoryParallelismHold(2, 1), (await mgr.GetWorkItemAsync(c))!.RepositoryParallelismHold);

        await MoveAsync(a, RemoteWorkItemStatus.Done);
        Assert.Empty((await PassAsync()).Claimed);
        Assert.Equal(new RepositoryParallelismHold(1, 1), (await mgr.GetWorkItemAsync(c))!.RepositoryParallelismHold);

        await MoveAsync(b, RemoteWorkItemStatus.Done);
        Assert.Equal(new[] { c }, Ids((await PassAsync()).Claimed));
    }

    [Theory]
    [InlineData(false, "No loop found for existing tags")]
    [InlineData(true, "Failed to start run: boom")]
    public async Task Start_anyway_hands_an_item_to_needs_you_the_way_the_scheduler_does(bool runFailsToStart, string reason)
    {
        var repo = AddRepo(parallelism: 1);
        await AddItemAsync(repo, RemoteWorkItemStatus.Running, "busy");
        var held = runFailsToStart
            ? await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "held")
            : await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "held", tags: NoLoopTag);
        await PassAsync();
        if (runFailsToStart)
            _engine.Setup(e => e.StartRunAsync(held, It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await StartAnywayAsync(held);

        Assert.Equal(HandStartResult.Started, result);
        Assert.Equal(RemoteWorkItemStatus.HumanFeedback, await StatusOfAsync(held));
        Assert.Equal(reason, (await _db.LoopRuns.GetWorkItemStatusReasonAsync(held))!.Text);
        var parked = (await _db.ServerClient.GetHistoryAsync(Opts, held, Ct))!
            .First(e => e.Field == "status");
        Assert.Equal(nameof(RemoteWorkItemStatus.HumanFeedback), parked.NewValue);
        Assert.Equal(reason, parked.Reason);
        _engine.Verify(e => e.StartRunAsync(held, It.IsAny<CancellationToken>()), Times.Exactly(runFailsToStart ? 1 : 0));
    }

    [Fact]
    public async Task Start_anyway_still_respects_the_instances_concurrency_cap()
    {
        var repo = AddRepo(parallelism: 1);
        var busy = await AddItemAsync(repo, RemoteWorkItemStatus.Running, "busy");
        SeedLiveRun(busy);
        var held = await AddItemAsync(repo, RemoteWorkItemStatus.Ready, "held");
        await PassAsync(maxConcurrent: 1);
        var history = await HistoryCountAsync(held);

        Assert.Equal(HandStartResult.AtConcurrencyLimit, await StartAnywayAsync(held, maxConcurrent: 1));
        await AssertUntouchedAsync(held, history);

        Assert.Equal(HandStartResult.Started, await StartAnywayAsync(held, maxConcurrent: 2));
        Assert.Equal(RemoteWorkItemStatus.Running, await StatusOfAsync(held));
    }

    [Theory]
    [InlineData(RemoteWorkItemStatus.Backlog)]
    [InlineData(RemoteWorkItemStatus.WorkQueue)]
    [InlineData(RemoteWorkItemStatus.Running)]
    [InlineData(RemoteWorkItemStatus.HumanFeedback)]
    [InlineData(RemoteWorkItemStatus.WaitingForIld)]
    [InlineData(RemoteWorkItemStatus.Done)]
    public async Task Start_anyway_refuses_an_item_that_is_not_ready_and_changes_nothing(RemoteWorkItemStatus status)
    {
        var repo = AddRepo(parallelism: 1);
        var item = await AddItemAsync(repo, status, "not ready");
        var history = await HistoryCountAsync(item);

        Assert.Equal(HandStartResult.NotReady, await StartAnywayAsync(item));

        Assert.Equal(status, await StatusOfAsync(item));
        Assert.Equal(history, await HistoryCountAsync(item));
        _engine.Verify(e => e.StartRunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Start_anyway_answers_not_found_for_an_unknown_item_and_starts_a_ready_item_nothing_holds_back()
    {
        Assert.Equal(HandStartResult.NotFound, await StartAnywayAsync("does-not-exist"));

        var free = await AddItemAsync(null, RemoteWorkItemStatus.Ready, "free");

        Assert.Equal(HandStartResult.Started, await StartAnywayAsync(free));
        Assert.Equal(RemoteWorkItemStatus.Running, await StatusOfAsync(free));
        _engine.Verify(e => e.StartRunAsync(free, It.IsAny<CancellationToken>()), Times.Once);
    }

    private RemoteWorkItemCoordinator CoordinatorSharing(ReadyClaimGate gate)
    {
        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, Guid.NewGuid(), Array.Empty<string>()));
        return new RemoteWorkItemCoordinator(_db.ServerClient, resolver.Object, _engine.Object, _db.LoopRuns,
            TestActors.Instance, providerStore: _db.Providers, workItemNotifier: _notifier.Object,
            repositoryHolds: _holds, claimGate: gate);
    }

    /// <summary>Holds the item's run start open until the test lets it go.</summary>
    private (Task reached, TaskCompletionSource release) BlockRunStart(string workItemId)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _engine.Setup(e => e.StartRunAsync(workItemId, It.IsAny<CancellationToken>()))
               .Returns(async () => { reached.TrySetResult(); await release.Task; });
        return (reached.Task, release);
    }

    [Fact]
    public async Task A_pass_waits_for_an_in_flight_start_anyway_and_counts_its_slot()
    {
        var gate = new ReadyClaimGate();
        var starter = CoordinatorSharing(gate);
        var scheduler = CoordinatorSharing(gate);
        var started = await AddItemAsync(null, RemoteWorkItemStatus.Ready, "started by hand");
        var (reached, release) = BlockRunStart(started);

        var start = starter.StartIgnoringRepositoryLimitAsync(Opts, started, 1, TestActors.Human, Ct);
        await reached;
        var other = await AddItemAsync(null, RemoteWorkItemStatus.Ready, "other");
        var pass = scheduler.RunPollCycleAsync(Opts, maxConcurrent: 1, claimReadyItems: true, Ct);
        Assert.False(pass.IsCompleted);

        SeedLiveRun(started);
        release.SetResult();
        Assert.Equal(HandStartResult.Started, await start);
        var result = await pass;

        Assert.Empty(result.Claimed);
        Assert.True(result.BlockedByCap);
        Assert.Equal(RemoteWorkItemStatus.Ready, await StatusOfAsync(other));
        _engine.Verify(e => e.StartRunAsync(other, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_start_anyway_waits_for_an_in_flight_pass_and_counts_what_it_claimed()
    {
        var gate = new ReadyClaimGate();
        var starter = CoordinatorSharing(gate);
        var scheduler = CoordinatorSharing(gate);
        var claimedByPass = await AddItemAsync(null, RemoteWorkItemStatus.Ready, "claimed by the pass");
        var (reached, release) = BlockRunStart(claimedByPass);

        var pass = scheduler.RunPollCycleAsync(Opts, maxConcurrent: 1, claimReadyItems: true, Ct);
        await reached;
        var byHand = await AddItemAsync(null, RemoteWorkItemStatus.Ready, "by hand");
        var history = await HistoryCountAsync(byHand);
        var start = starter.StartIgnoringRepositoryLimitAsync(Opts, byHand, 1, TestActors.Human, Ct);
        Assert.False(start.IsCompleted);

        SeedLiveRun(claimedByPass);
        release.SetResult();
        Assert.Equal(new[] { claimedByPass }, Ids((await pass).Claimed));

        Assert.Equal(HandStartResult.AtConcurrencyLimit, await start);
        await AssertUntouchedAsync(byHand, history);
    }

    [Fact]
    public async Task A_start_anyway_that_waited_for_a_pass_while_shutdown_began_starts_nothing()
    {
        var gate = new ReadyClaimGate();
        var shutdown = new ShutdownState();
        var scheduler = CoordinatorSharing(gate);
        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, Guid.NewGuid(), Array.Empty<string>()));
        var starter = new RemoteWorkItemCoordinator(_db.ServerClient, resolver.Object, _engine.Object, _db.LoopRuns,
            TestActors.Instance, providerStore: _db.Providers, workItemNotifier: _notifier.Object,
            repositoryHolds: _holds, claimGate: gate, shutdown: shutdown);
        var claimedByPass = await AddItemAsync(null, RemoteWorkItemStatus.Ready, "claimed by the pass");
        var (reached, release) = BlockRunStart(claimedByPass);

        var pass = scheduler.RunPollCycleAsync(Opts, maxConcurrent: 10, claimReadyItems: true, Ct);
        await reached;
        var byHand = await AddItemAsync(null, RemoteWorkItemStatus.Ready, "by hand");
        var history = await HistoryCountAsync(byHand);
        var start = starter.StartIgnoringRepositoryLimitAsync(Opts, byHand, 10, TestActors.Human, Ct);
        Assert.False(start.IsCompleted);

        shutdown.SignalStopping();
        release.SetResult();
        await pass;

        Assert.Equal(HandStartResult.ShuttingDown, await start);
        await AssertUntouchedAsync(byHand, history);
    }
}

/// <summary>
/// Repository limits through a scripted server, where what the coordinator asks
/// for — and what fails — is under the test's control.
/// </summary>
public sealed class RepositoryParallelismScriptedServerTests
{
    private static readonly WorkItemServerOptions Opts = new() { BaseUrl = "http://x", ApiKey = "k" };

    private static RemoteWorkItem Ready(string id, Guid? repo) => new()
    {
        Id = id, Title = "w", Status = RemoteWorkItemStatus.Ready, Tags = new[] { "build" }, RepositoryId = repo,
    };

    private static Mock<ILoopRunStore> NoLiveRuns()
    {
        var store = new Mock<ILoopRunStore>();
        store.Setup(s => s.GetActiveWorkItemIdsAsync()).ReturnsAsync(Array.Empty<string>());
        return store;
    }

    private static Mock<ILoopTemplateResolver> ResolvesEverything()
    {
        var resolver = new Mock<ILoopTemplateResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<IReadOnlyList<string>>()))
                .Returns(new LoopTemplateResolution(LoopTemplateResolutionKind.Single, Guid.NewGuid(), Array.Empty<string>()));
        return resolver;
    }

    private static Mock<IWorkItemServerClient> ServerWith(params RemoteWorkItem[] ready)
    {
        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.PollAsync(Opts, It.IsAny<IReadOnlyList<string>>(), It.IsAny<WorkItemActor>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemotePollResponse { ReadyItems = ready });
        client.Setup(c => c.TransitionAsync(Opts, It.IsAny<string>(),
                It.Is<RemoteTransitionRequest>(r => r.TargetStatus == RemoteWorkItemStatus.Running),
                It.IsAny<WorkItemActor>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemoteTransitionResponse { Success = true, ActualStatus = RemoteWorkItemStatus.Running });
        return client;
    }

    private static Repository Repo(int parallelism) => new()
    {
        Id = Guid.NewGuid(), Name = "repo", CloneUrl = "https://example.com/repo.git", Parallelism = parallelism,
    };

    [Fact]
    public async Task Without_a_limited_repository_the_pass_makes_no_extra_server_calls_and_claims_as_before()
    {
        var unlimited = Repo(parallelism: 0);
        var items = new[] { Ready("1", unlimited.Id), Ready("2", null), Ready("3", unlimited.Id) };
        var client = ServerWith(items);
        var providers = new Mock<IProviderStore>();
        providers.Setup(p => p.GetRepositoryByIdAsync(unlimited.Id)).ReturnsAsync(unlimited);
        var engine = new Mock<ILoopEngine>();

        var sut = new RemoteWorkItemCoordinator(client.Object, ResolvesEverything().Object, engine.Object,
            NoLiveRuns().Object, TestActors.Instance, providerStore: providers.Object);
        var result = await sut.RunPollCycleAsync(Opts, maxConcurrent: 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "1", "2" }, result.Claimed.Select(w => w.Id));
        Assert.True(result.BlockedByCap);
        Assert.Empty(result.HeldByRepositoryLimit);
        client.Verify(c => c.ListAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<RemoteWorkItemStatus?>(),
            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_pass_that_cannot_count_the_slots_claims_nothing_for_a_limited_repository(bool listFails)
    {
        var limited = Repo(parallelism: 1);
        var item = Ready("1", limited.Id);
        var client = ServerWith(item);
        var providers = new Mock<IProviderStore>();
        if (listFails)
        {
            providers.Setup(p => p.GetRepositoryByIdAsync(limited.Id)).ReturnsAsync(limited);
            client.Setup(c => c.ListAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<RemoteWorkItemStatus?>(),
                    It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new HttpRequestException("server unavailable"));
        }
        else
        {
            providers.Setup(p => p.GetRepositoryByIdAsync(limited.Id)).ThrowsAsync(new InvalidOperationException("database unavailable"));
            client.Setup(c => c.ListAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<RemoteWorkItemStatus?>(),
                    It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(Array.Empty<RemoteWorkItem>());
        }
        var engine = new Mock<ILoopEngine>();

        var sut = new RemoteWorkItemCoordinator(client.Object, ResolvesEverything().Object, engine.Object,
            NoLiveRuns().Object, TestActors.Instance, providerStore: providers.Object);
        try
        {
            await sut.RunPollCycleAsync(Opts, maxConcurrent: 5, ct: TestContext.Current.CancellationToken);
        }
        catch (Exception)
        {
            // Failing the pass is one way to fail closed; the scheduler retries.
        }

        client.Verify(c => c.TransitionAsync(Opts, item.Id, It.IsAny<RemoteTransitionRequest>(),
            It.IsAny<WorkItemActor>(), It.IsAny<CancellationToken>()), Times.Never);
        engine.Verify(e => e.StartRunAsync(item.Id, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Start_anyway_of_an_item_claimed_elsewhere_between_read_and_claim_is_refused_and_starts_nothing()
    {
        var item = Ready("1", null);
        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.GetAsync(Opts, item.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        client.Setup(c => c.TransitionAsync(Opts, item.Id, It.IsAny<RemoteTransitionRequest>(),
                It.IsAny<WorkItemActor>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new RemoteTransitionResponse { Success = false, ActualStatus = RemoteWorkItemStatus.Running });
        var engine = new Mock<ILoopEngine>();
        var notifier = new Mock<IWorkItemNotifier>();

        var sut = new RemoteWorkItemCoordinator(client.Object, ResolvesEverything().Object, engine.Object,
            NoLiveRuns().Object, TestActors.Instance, providerStore: new Mock<IProviderStore>().Object,
            workItemNotifier: notifier.Object);
        var result = await sut.StartIgnoringRepositoryLimitAsync(Opts, item.Id, 5, TestActors.Human, TestContext.Current.CancellationToken);

        Assert.Equal(HandStartResult.NotReady, result);
        engine.Verify(e => e.StartRunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        notifier.Verify(n => n.WorkItemStateChangedAsync(It.IsAny<string>(), It.IsAny<RemoteWorkItemStatus>(),
            It.IsAny<RemoteWorkItemStatus>()), Times.Never);
    }
}
