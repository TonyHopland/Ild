using System.Net;
using ILD.Core.Services.Remote;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// Verifies the typed HTTP client lines up with the live server. It runs against the
/// same test host as the API tests so these never go stale relative to the
/// server contract.
/// </summary>
public sealed class WorkItemServerClientTests : IClassFixture<WorkItemServerClientTests.Server>
{
    private const string ApiKey = "test-key";

    public sealed class Server() : WorkItemServerFactory(ApiKey);

    private readonly WorkItemServerClient _client;
    private readonly WorkItemServerOptions _opts = new() { BaseUrl = "http://localhost", ApiKey = ApiKey };

    public WorkItemServerClientTests(Server server) => _client = new WorkItemServerClient(server.CreateClient());

    [Fact]
    public async Task Create_then_get_round_trips_via_client()
    {
        var created = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "client-roundtrip",
            Tags = new[] { "alpha", "beta" },
        }, TestActors.Human, TestContext.Current.CancellationToken);

        Assert.Equal("client-roundtrip", created.Title);
        Assert.Equal(new[] { "alpha", "beta" }, created.Tags);

        var fetched = await _client.GetAsync(_opts, created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(RemoteWorkItemStatus.Backlog, fetched!.Status);
    }

    [Fact]
    public async Task The_order_value_round_trips_through_create_and_update()
    {
        var ct = TestContext.Current.CancellationToken;
        var older = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "older",
        }, TestActors.Human, ct);
        var newer = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "newer",
        }, TestActors.Human, ct);
        Assert.True(newer.Priority < older.Priority);

        var moved = await _client.UpdateAsync(_opts, newer.Id, new RemoteUpdateWorkItemRequest { Priority = older.Priority + 0.5 }, TestActors.Human, ct);
        Assert.Equal(older.Priority + 0.5, moved!.Priority);

        var renamed = await _client.UpdateAsync(_opts, newer.Id, new RemoteUpdateWorkItemRequest { Title = "renamed" }, TestActors.Human, ct);
        Assert.Equal(older.Priority + 0.5, renamed!.Priority);
        Assert.Equal(older.Priority + 0.5, (await _client.GetAsync(_opts, newer.Id, ct))!.Priority);
    }

    [Fact]
    public async Task Custom_branch_name_round_trips_through_create_get_and_update()
    {
        var created = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "custom-branch",
            BranchNameOverride = "feature/foo",
        }, TestActors.Human, TestContext.Current.CancellationToken);
        Assert.Equal("feature/foo", created.BranchNameOverride);
        Assert.Equal("feature/foo", (await _client.GetAsync(_opts, created.Id, TestContext.Current.CancellationToken))!.BranchNameOverride);

        var renamed = await _client.UpdateAsync(_opts, created.Id, new RemoteUpdateWorkItemRequest
        {
            BranchNameOverride = "feature/bar",
        }, TestActors.Human, TestContext.Current.CancellationToken);
        Assert.Equal("feature/bar", renamed!.BranchNameOverride);

        var cleared = await _client.UpdateAsync(_opts, created.Id, new RemoteUpdateWorkItemRequest
        {
            BranchNameOverride = "",
        }, TestActors.Human, TestContext.Current.CancellationToken);
        Assert.Null(cleared!.BranchNameOverride);
    }

    [Fact]
    public async Task Base_branch_round_trips_through_create_get_and_update()
    {
        var created = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "base-branch",
            BaseBranchOverride = "release/1.0",
        }, TestActors.Human, TestContext.Current.CancellationToken);
        Assert.Equal("release/1.0", created.BaseBranchOverride);
        Assert.Equal("release/1.0", (await _client.GetAsync(_opts, created.Id, TestContext.Current.CancellationToken))!.BaseBranchOverride);

        var moved = await _client.UpdateAsync(_opts, created.Id, new RemoteUpdateWorkItemRequest
        {
            BaseBranchOverride = "release/2.0",
        }, TestActors.Human, TestContext.Current.CancellationToken);
        Assert.Equal("release/2.0", moved!.BaseBranchOverride);

        var cleared = await _client.UpdateAsync(_opts, created.Id, new RemoteUpdateWorkItemRequest
        {
            BaseBranchOverride = "",
        }, TestActors.Human, TestContext.Current.CancellationToken);
        Assert.Null(cleared!.BaseBranchOverride);
    }

    private async Task<string> CreateAtAsync(WorkItemServerClient client, string title, double priority, RemoteWorkItemStatus status = RemoteWorkItemStatus.Backlog)
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await client.CreateAsync(_opts, new RemoteCreateWorkItemRequest { Title = title, ForceStatus = status }, TestActors.Human, ct);
        await client.UpdateAsync(_opts, created.Id, new RemoteUpdateWorkItemRequest { Priority = priority }, TestActors.Human, ct);
        return created.Id;
    }

    [Fact]
    public async Task A_reorder_within_a_column_round_trips_the_placed_item()
    {
        // Own host: it orders and renumbers the whole board by priority.
        await using var ownHost = new Server();
        var own = new WorkItemServerClient(ownHost.CreateClient());
        var ct = TestContext.Current.CancellationToken;
        var a = await CreateAtAsync(own, "a", 0);
        var b = await CreateAtAsync(own, "b", 1);
        var c = await CreateAtAsync(own, "c", 2);

        var resp = await own.ReorderAsync(_opts, c, new RemoteReorderWorkItemRequest { AboveId = a, BelowId = b }, TestActors.Human, ct);

        Assert.Equal(RemoteReorderOutcome.Moved, resp.Outcome);
        Assert.False(resp.Renumbered);
        Assert.Equal(c, resp.Item!.Id);
        Assert.Equal(RemoteWorkItemStatus.Backlog, resp.Item.Status);
        Assert.Equal(0.5, resp.Item.Priority);
        Assert.Equal(0.5, (await own.GetAsync(_opts, c, ct))!.Priority);
    }

    [Fact]
    public async Task A_reorder_into_another_status_round_trips_the_status_and_the_reason()
    {
        // Own host: it orders and renumbers the whole board by priority.
        await using var ownHost = new Server();
        var own = new WorkItemServerClient(ownHost.CreateClient());
        var ct = TestContext.Current.CancellationToken;
        var a = await CreateAtAsync(own, "a", 0, RemoteWorkItemStatus.Ready);
        var x = await CreateAtAsync(own, "x", 5);

        var resp = await own.ReorderAsync(_opts, x, new RemoteReorderWorkItemRequest
        {
            BelowId = a,
            TargetStatus = RemoteWorkItemStatus.Ready,
            Reason = "dropped on the board",
        }, TestActors.Human, ct);

        Assert.Equal(RemoteReorderOutcome.Moved, resp.Outcome);
        Assert.Equal(RemoteWorkItemStatus.Ready, resp.Item!.Status);
        Assert.True(resp.Item.Priority < 0);
        var status = Assert.Single((await own.GetHistoryAsync(_opts, x, ct))!, e => e.Field == "status" && e.NewValue == "Ready");
        Assert.Equal("dropped on the board", status.Reason);
    }

    [Fact]
    public async Task A_reorder_that_renumbers_says_so()
    {
        // Own host: it orders and renumbers the whole board by priority.
        await using var ownHost = new Server();
        var own = new WorkItemServerClient(ownHost.CreateClient());
        var ct = TestContext.Current.CancellationToken;
        var a = await CreateAtAsync(own, "a", 1);
        var b = await CreateAtAsync(own, "b", 1);
        var c = await CreateAtAsync(own, "c", 9);

        var resp = await own.ReorderAsync(_opts, c, new RemoteReorderWorkItemRequest { AboveId = a, BelowId = b }, TestActors.Human, ct);

        Assert.Equal(RemoteReorderOutcome.Moved, resp.Outcome);
        Assert.True(resp.Renumbered);
        var order = (await own.ListAsync(_opts, status: null, tags: null, ct)).Select(w => w.Id);
        Assert.Equal(new[] { a, c, b }, order);
    }

    [Fact]
    public async Task A_reorder_that_cannot_place_answers_its_outcome_and_a_refused_move_its_reason()
    {
        // Own host: it orders and renumbers the whole board by priority.
        await using var ownHost = new Server();
        var own = new WorkItemServerClient(ownHost.CreateClient());
        var ct = TestContext.Current.CancellationToken;
        var dependency = await CreateAtAsync(own, "dependency", 0);
        var a = await CreateAtAsync(own, "a", 1);
        var blocked = (await own.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "blocked",
            ForceStatus = RemoteWorkItemStatus.Ready,
            Dependencies = new[] { dependency },
        }, TestActors.Human, ct)).Id;

        var missing = await own.ReorderAsync(_opts, "999", new RemoteReorderWorkItemRequest { AboveId = a }, TestActors.Human, ct);
        var nowhere = await own.ReorderAsync(_opts, a, new RemoteReorderWorkItemRequest { AboveId = "999" }, TestActors.Human, ct);
        var refused = await own.ReorderAsync(_opts, blocked, new RemoteReorderWorkItemRequest
        {
            AboveId = a,
            TargetStatus = RemoteWorkItemStatus.Running,
        }, TestActors.Human, ct);

        Assert.Equal(RemoteReorderOutcome.NotFound, missing.Outcome);
        Assert.Equal(RemoteReorderOutcome.InvalidNeighbour, nowhere.Outcome);
        Assert.Equal(RemoteReorderOutcome.TransitionRefused, refused.Outcome);
        Assert.Equal("Dependencies not satisfied", refused.Reason);
        Assert.Null(missing.Item);
        Assert.Equal(1, (await own.GetAsync(_opts, a, ct))!.Priority);
        Assert.Equal(RemoteWorkItemStatus.Ready, (await own.GetAsync(_opts, blocked, ct))!.Status);
    }

    [Fact]
    public async Task Get_returns_null_on_not_found()
    {
        var item = await _client.GetAsync(_opts, Guid.NewGuid().ToString(), TestContext.Current.CancellationToken);
        Assert.Null(item);
    }

    [Fact]
    public async Task Transition_to_running_succeeds_when_ready()
    {
        var created = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "tx", ForceStatus = RemoteWorkItemStatus.Ready,
        }, TestActors.Human, TestContext.Current.CancellationToken);
        var resp = await _client.TransitionAsync(_opts, created.Id, new RemoteTransitionRequest
        {
            TargetStatus = RemoteWorkItemStatus.Running,
        }, TestActors.Human, TestContext.Current.CancellationToken);
        Assert.True(resp.Success);
        Assert.Equal(RemoteWorkItemStatus.Running, resp.ActualStatus);
    }

    [Fact]
    public async Task Poll_returns_ready_items_and_heartbeats_active()
    {
        var ready = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "ready", ForceStatus = RemoteWorkItemStatus.Ready,
        }, TestActors.Human, TestContext.Current.CancellationToken);
        var running = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "running", ForceStatus = RemoteWorkItemStatus.Ready,
        }, TestActors.Human, TestContext.Current.CancellationToken);
        await _client.TransitionAsync(_opts, running.Id,
            new RemoteTransitionRequest { TargetStatus = RemoteWorkItemStatus.Running }, TestActors.Human, TestContext.Current.CancellationToken);

        var poll = await _client.PollAsync(_opts, new[] { running.Id }, TestActors.Human, TestContext.Current.CancellationToken);
        Assert.Contains(ready.Id, poll.ReadyItems.Select(x => x.Id));
        Assert.Contains(running.Id, poll.ActiveItems.Select(x => x.Id));
    }

    [Fact]
    public async Task List_filters_by_status()
    {
        await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest { Title = "a" }, TestActors.Human, TestContext.Current.CancellationToken);
        await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest { Title = "b", ForceStatus = RemoteWorkItemStatus.Ready }, TestActors.Human, TestContext.Current.CancellationToken);

        var ready = await _client.ListAsync(_opts, RemoteWorkItemStatus.Ready, null, TestContext.Current.CancellationToken);
        Assert.NotEmpty(ready);
        Assert.All(ready, w => Assert.Equal(RemoteWorkItemStatus.Ready, w.Status));
    }

    [Fact]
    public async Task A_refused_key_names_the_server_that_refused_it_and_the_setting_to_look_at()
    {
        // Callers turn this exception into "WorkItemServer unreachable", so the
        // message is the only thing distinguishing a key mismatch from a server
        // that is down — and, where two are running (a preview of ILD runs its
        // own next to the host's), which of them answered.
        var wrongKey = new WorkItemServerOptions { BaseUrl = _opts.BaseUrl, ApiKey = "not-the-configured-key" };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => _client.ListAsync(wrongKey, null, null, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains($"{_opts.BaseUrl}/workitems", ex.Message);
        Assert.Contains("WORKITEM_API_KEYS", ex.Message);
    }
}
