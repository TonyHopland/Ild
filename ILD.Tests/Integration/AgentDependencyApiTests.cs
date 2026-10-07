using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests.Integration;

/// <summary>
/// Work item dependencies from the agent surface: changed directly on an item
/// the caller's session created, and proposed for a human to approve on any
/// other. The WorkItem server is held here, not inside the factory, so a test
/// can read its stored edges and run its work-queue reconcile pass.
/// </summary>
public class AgentDependencyApiTests
{
    private const string RunHeader = "X-ILD-Run-Id";
    private const string ChatHeader = "X-ILD-Chat-Session-Id";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Host : IAsyncDisposable
    {
        public FakeWorkItemServerHarness Server { get; } = new();
        public ApiFactory Factory { get; }
        public HttpClient Human { get; private set; } = null!;
        public Guid RepositoryId { get; private set; }

        public Host()
        {
            Factory = new ApiFactory(configureServices: services =>
            {
                services.ReplaceSingleton(Server.Client);
                services.ReplaceSingleton(new Mock<IWorkItemNotifier>().Object);
                services.ReplaceSingleton(new Mock<IChatNotifier>().Object);
            });
        }

        public async Task<Host> StartAsync()
        {
            Human = await Factory.CreateAuthenticatedClientAsync();
            RepositoryId = await SeedRepositoryAsync(Factory);
            return this;
        }

        public HttpClient Agent(Guid? runId = null, Guid? chatSessionId = null)
        {
            var client = Factory.CreateClient();
            var token = Factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (runId is { } run) client.DefaultRequestHeaders.Add(RunHeader, run.ToString());
            if (chatSessionId is { } chat) client.DefaultRequestHeaders.Add(ChatHeader, chat.ToString());
            return client;
        }

        /// <summary>The item's edges as the WorkItem server stores them.</summary>
        public async Task<string[]> StoredDependenciesAsync(string id)
            => (await Server.Service.GetDependenciesAsync(id, Ct))!.ToArray();

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            Server.Dispose();
        }
    }

    private static async Task<Host> StartHostAsync() => await new Host().StartAsync();

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
        => JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct)).RootElement;

    private static bool IsNullOrAbsent(JsonElement obj, string property)
        => !obj.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null;

    private static async Task<string> CreateHumanItemAsync(Host host, string title)
    {
        var resp = await host.Human.PostAsJsonAsync("/api/v1/workitems", new
        {
            title,
            description = "",
            repositoryId = host.RepositoryId.ToString(),
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await ReadJsonAsync(resp)).GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateAgentItemAsync(Host host, HttpClient agent, string title, params string[] dependencies)
    {
        var resp = await agent.PostAsJsonAsync("/api/v1/agent/workitems", new
        {
            title,
            description = "",
            repositoryId = host.RepositoryId.ToString(),
            dependencies,
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await ReadJsonAsync(resp)).GetProperty("id").GetString()!;
    }

    private static async Task HumanAddsAsync(Host host, string id, string dependsOn)
        => (await host.Human.PostAsJsonAsync($"/api/v1/workitems/{id}/dependencies", new { dependencyId = dependsOn }, Ct))
            .EnsureSuccessStatusCode();

    private static async Task HumanRemovesAsync(Host host, string id, string dependsOn)
        => (await host.Human.DeleteAsync($"/api/v1/workitems/{id}/dependencies/{dependsOn}", Ct)).EnsureSuccessStatusCode();

    private static async Task ToWorkQueueAsync(Host host, string id)
        => (await host.Human.PostAsJsonAsync($"/api/v1/workitems/{id}/transition", new { targetStatus = "WorkQueue" }, Ct))
            .EnsureSuccessStatusCode();

    private static async Task<JsonElement> GetItemAsync(Host host, string id)
    {
        var resp = await host.Human.GetAsync($"/api/v1/workitems/{id}", Ct);
        resp.EnsureSuccessStatusCode();
        return await ReadJsonAsync(resp);
    }

    private static async Task<string> StatusOfAsync(Host host, string id)
        => (await GetItemAsync(host, id)).GetProperty("status").GetString()!;

    private static Task<HttpResponseMessage> AddAsync(HttpClient agent, string id, string dependsOn)
        => agent.PostAsJsonAsync($"/api/v1/agent/workitems/{id}/dependencies", new { dependsOnWorkItemId = dependsOn }, Ct);

    private static Task<HttpResponseMessage> RemoveAsync(HttpClient agent, string id, string dependsOn)
        => agent.DeleteAsync($"/api/v1/agent/workitems/{id}/dependencies/{dependsOn}", Ct);

    private static (string Id, string Title, string Status)[] DependenciesIn(JsonElement body)
        => body.GetProperty("dependencies").EnumerateArray()
            .Select(d => (d.GetProperty("id").GetString()!, d.GetProperty("title").GetString()!, d.GetProperty("status").GetString()!))
            .ToArray();

    private static async Task<string> ErrorOfAsync(HttpResponseMessage resp)
        => (await ReadJsonAsync(resp)).GetProperty("error").GetString()!;

    // -- Direct changes on the caller's own items -------------------------------

    public enum Caller { LoopRun, ChatSession }

    [Theory]
    [InlineData(Caller.LoopRun)]
    [InlineData(Caller.ChatSession)]
    public async Task An_agent_adds_and_removes_a_dependency_on_an_item_its_session_created(Caller caller)
    {
        await using var host = await StartHostAsync();
        var agent = caller == Caller.LoopRun
            ? host.Agent(runId: await SeedRunAsync(host.Factory))
            : host.Agent(chatSessionId: await SeedChatSessionAsync(host.Factory));
        var itemId = await CreateAgentItemAsync(host, agent, "Agent's item");
        var otherId = await CreateHumanItemAsync(host, "Chat tab");

        var added = await AddAsync(agent, itemId, otherId);

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var addBody = await ReadJsonAsync(added);
        Assert.True(addBody.GetProperty("changed").GetBoolean());
        Assert.Equal(new[] { (otherId, "Chat tab", "Backlog") }, DependenciesIn(addBody));
        Assert.Equal(new[] { otherId }, await host.StoredDependenciesAsync(itemId));
        Assert.Empty(await host.StoredDependenciesAsync(otherId));

        var removed = await RemoveAsync(agent, itemId, otherId);

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var removeBody = await ReadJsonAsync(removed);
        Assert.True(removeBody.GetProperty("changed").GetBoolean());
        Assert.Empty(DependenciesIn(removeBody));
        Assert.Empty(await host.StoredDependenciesAsync(itemId));
        Assert.Empty(await host.StoredDependenciesAsync(otherId));
    }

    public enum NotOwned { OtherRunsItem, OtherChatsItem, HumansItem, NoSessionHeader }

    [Theory]
    [InlineData(NotOwned.OtherRunsItem)]
    [InlineData(NotOwned.OtherChatsItem)]
    [InlineData(NotOwned.HumansItem)]
    [InlineData(NotOwned.NoSessionHeader)]
    public async Task Neither_tool_changes_an_item_the_session_did_not_create_and_both_point_at_proposing(NotOwned situation)
    {
        await using var host = await StartHostAsync();
        var existingDep = await CreateHumanItemAsync(host, "Existing dependency");
        var candidate = await CreateHumanItemAsync(host, "Candidate");
        var runA = await SeedRunAsync(host.Factory);
        var runB = await SeedRunAsync(host.Factory);
        var chatA = await SeedChatSessionAsync(host.Factory);
        var chatB = await SeedChatSessionAsync(host.Factory);

        string itemId;
        HttpClient caller;
        switch (situation)
        {
            case NotOwned.OtherRunsItem:
                itemId = await CreateAgentItemAsync(host, host.Agent(runId: runA), "Run A's item", existingDep);
                caller = host.Agent(runId: runB);
                break;
            case NotOwned.OtherChatsItem:
                itemId = await CreateAgentItemAsync(host, host.Agent(chatSessionId: chatA), "Chat A's item", existingDep);
                caller = host.Agent(chatSessionId: chatB);
                break;
            case NotOwned.HumansItem:
                itemId = await CreateHumanItemAsync(host, "Human's item");
                await HumanAddsAsync(host, itemId, existingDep);
                caller = host.Agent(runId: runA);
                break;
            default:
                itemId = await CreateAgentItemAsync(host, host.Agent(runId: runA), "Run A's item", existingDep);
                caller = host.Agent();
                break;
        }

        var add = await AddAsync(caller, itemId, candidate);
        var remove = await RemoveAsync(caller, itemId, existingDep);

        Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);
        Assert.Contains("propose_workitem_edit", await ErrorOfAsync(add));
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        Assert.Contains("propose_workitem_edit", await ErrorOfAsync(remove));
        Assert.Equal(new[] { existingDep }, await host.StoredDependenciesAsync(itemId));
        Assert.Empty(await host.StoredDependenciesAsync(candidate));
    }

    [Fact]
    public async Task An_unknown_item_is_404_for_both_tools()
    {
        await using var host = await StartHostAsync();
        var agent = host.Agent(runId: await SeedRunAsync(host.Factory));
        var otherId = await CreateHumanItemAsync(host, "Other");
        var unknown = Guid.NewGuid().ToString();

        Assert.Equal(HttpStatusCode.NotFound, (await AddAsync(agent, unknown, otherId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RemoveAsync(agent, unknown, otherId)).StatusCode);
    }

    public enum BadTarget { Unknown, Blank, ItemItself }

    [Theory]
    [InlineData(BadTarget.Unknown, true)]
    [InlineData(BadTarget.Unknown, false)]
    [InlineData(BadTarget.ItemItself, true)]
    [InlineData(BadTarget.ItemItself, false)]
    [InlineData(BadTarget.Blank, true)]
    public async Task A_bad_other_item_is_a_400_that_changes_nothing(BadTarget target, bool adding)
    {
        await using var host = await StartHostAsync();
        var agent = host.Agent(runId: await SeedRunAsync(host.Factory));
        var existingDep = await CreateHumanItemAsync(host, "Existing dependency");
        var itemId = await CreateAgentItemAsync(host, agent, "Agent's item", existingDep);
        var dependsOn = target switch
        {
            BadTarget.Unknown => Guid.NewGuid().ToString(),
            BadTarget.Blank => "   ",
            _ => itemId,
        };

        var resp = adding ? await AddAsync(agent, itemId, dependsOn) : await RemoveAsync(agent, itemId, dependsOn);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(new[] { existingDep }, await host.StoredDependenciesAsync(itemId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adding_an_edge_that_would_close_a_cycle_is_a_400_that_says_so(bool transitive)
    {
        await using var host = await StartHostAsync();
        var agent = host.Agent(runId: await SeedRunAsync(host.Factory));
        var itemId = await CreateAgentItemAsync(host, agent, "Agent's item");
        var waitsOnItem = await CreateHumanItemAsync(host, "Waits on the agent's item");
        await HumanAddsAsync(host, waitsOnItem, itemId);
        var target = waitsOnItem;
        if (transitive)
        {
            target = await CreateHumanItemAsync(host, "Waits further down");
            await HumanAddsAsync(host, target, waitsOnItem);
        }

        var resp = await AddAsync(agent, itemId, target);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("cycle", await ErrorOfAsync(resp), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await host.StoredDependenciesAsync(itemId));
    }

    [Fact]
    public async Task Adding_an_existing_edge_or_removing_an_absent_one_is_a_no_op_that_says_so()
    {
        await using var host = await StartHostAsync();
        var agent = host.Agent(chatSessionId: await SeedChatSessionAsync(host.Factory));
        var existingDep = await CreateHumanItemAsync(host, "Existing dependency");
        var notADep = await CreateHumanItemAsync(host, "Not a dependency");
        var itemId = await CreateAgentItemAsync(host, agent, "Agent's item", existingDep);

        foreach (var resp in new[] { await AddAsync(agent, itemId, existingDep), await RemoveAsync(agent, itemId, notADep) })
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await ReadJsonAsync(resp);
            Assert.False(body.GetProperty("changed").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
            Assert.Equal(new[] { (existingDep, "Existing dependency", "Backlog") }, DependenciesIn(body));
        }
        Assert.Equal(new[] { existingDep }, await host.StoredDependenciesAsync(itemId));
    }

    [Fact]
    public async Task Removing_the_last_unfinished_dependency_promotes_a_work_queue_item_the_same_as_the_human_route()
    {
        await using var host = await StartHostAsync();
        var agent = host.Agent(runId: await SeedRunAsync(host.Factory));
        var blocker = await CreateHumanItemAsync(host, "Unfinished blocker");
        var agentsItem = await CreateAgentItemAsync(host, agent, "Agent's item", blocker);
        var humansItem = await CreateHumanItemAsync(host, "Human's item");
        await HumanAddsAsync(host, humansItem, blocker);
        await ToWorkQueueAsync(host, agentsItem);
        await ToWorkQueueAsync(host, humansItem);
        Assert.Equal("WorkQueue", await StatusOfAsync(host, agentsItem));
        Assert.Equal("WorkQueue", await StatusOfAsync(host, humansItem));

        Assert.Equal(HttpStatusCode.OK, (await RemoveAsync(agent, agentsItem, blocker)).StatusCode);
        await HumanRemovesAsync(host, humansItem, blocker);

        Assert.Equal(await StatusOfAsync(host, humansItem), await StatusOfAsync(host, agentsItem));

        await host.Server.Service.ReconcileWorkQueueAsync(Ct);

        Assert.Equal("Ready", await StatusOfAsync(host, agentsItem));
        Assert.Equal("Ready", await StatusOfAsync(host, humansItem));
    }

    // -- Proposed changes on any item --------------------------------------------

    private static Task<HttpResponseMessage> ProposeAsync(HttpClient agent, string itemId, object body)
        => agent.PostAsJsonAsync($"/api/v1/agent/workitems/{itemId}/edit-proposals", body, Ct);

    private static async Task<string> ProposeOkAsync(HttpClient agent, string itemId, object body)
    {
        var resp = await ProposeAsync(agent, itemId, body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await ReadJsonAsync(resp)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement[]> ListAsHumanAsync(Host host, string itemId)
    {
        var resp = await host.Human.GetAsync($"/api/v1/workitems/{itemId}/edit-proposals", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await ReadJsonAsync(resp)).EnumerateArray().ToArray();
    }

    private static async Task<JsonElement> ReadProposalAsync(Host host, string itemId, string proposalId)
        => Assert.Single(await ListAsHumanAsync(host, itemId), p => p.GetProperty("id").GetString() == proposalId);

    private static async Task<string> ProposalStatusAsync(Host host, string itemId, string proposalId)
        => (await ReadProposalAsync(host, itemId, proposalId)).GetProperty("status").GetString()!;

    private static Task<HttpResponseMessage> ApproveAsync(Host host, string itemId, string proposalId)
        => host.Human.PostAsync($"/api/v1/workitems/{itemId}/edit-proposals/{proposalId}/approve", null, Ct);

    private static (string Id, string? Title)[] NamedItems(JsonElement fields, string property)
        => fields.GetProperty(property).EnumerateArray()
            .Select(d => (d.GetProperty("id").GetString()!, IsNullOrAbsent(d, "title") ? null : d.GetProperty("title").GetString()))
            .ToArray();

    private static string[] Ids(JsonElement array)
        => array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    public async Task A_dependency_only_proposal_changes_nothing_until_approved_and_then_applies_exactly_its_changes()
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host, "Human's item");
        var dropped = await CreateHumanItemAsync(host, "Dropped dependency");
        var added = await CreateHumanItemAsync(host, "Chat tab");
        await HumanAddsAsync(host, itemId, dropped);
        var agent = host.Agent(runId: await SeedRunAsync(host.Factory));
        var otherPending = await ProposeOkAsync(agent, itemId, new { title = "Another proposal" });

        var proposalId = await ProposeOkAsync(agent, itemId, new
        {
            addDependencies = new[] { added },
            removeDependencies = new[] { dropped },
            rationale = "The plan changed.",
        });

        Assert.Equal(new[] { dropped }, await host.StoredDependenciesAsync(itemId));
        Assert.Equal(2, (await GetItemAsync(host, itemId)).GetProperty("pendingEditProposalCount").GetInt32());

        var asHuman = await ReadProposalAsync(host, itemId, proposalId);
        var agentList = await agent.GetAsync($"/api/v1/agent/workitems/{itemId}/edit-proposals", Ct);
        Assert.Equal(HttpStatusCode.OK, agentList.StatusCode);
        var asAgent = Assert.Single((await ReadJsonAsync(agentList)).EnumerateArray(), p => p.GetProperty("id").GetString() == proposalId);
        foreach (var read in new[] { asHuman, asAgent })
        {
            Assert.Equal(new[] { (added, (string?)"Chat tab") }, NamedItems(read.GetProperty("proposed"), "addDependencies"));
            Assert.Equal(new[] { (dropped, (string?)"Dropped dependency") }, NamedItems(read.GetProperty("proposed"), "removeDependencies"));
            Assert.Equal(new[] { dropped }, Ids(read.GetProperty("snapshot").GetProperty("dependencies")));
        }

        var resp = await ApproveAsync(host, itemId, proposalId);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var decided = (await ReadJsonAsync(resp)).GetProperty("proposal");
        Assert.Equal("Approved", decided.GetProperty("status").GetString());
        Assert.Equal(new[] { (added, (string?)"Chat tab") }, NamedItems(decided.GetProperty("proposed"), "addDependencies"));
        Assert.Equal(new[] { added }, await host.StoredDependenciesAsync(itemId));
        Assert.Equal("Human's item", (await GetItemAsync(host, itemId)).GetProperty("title").GetString());
        Assert.Equal("Stale", await ProposalStatusAsync(host, itemId, otherPending));
    }

    [Fact]
    public async Task A_proposed_removal_goes_stale_when_the_dependency_set_changed_and_keeps_the_human_edge()
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host, "Human's item");
        var dropped = await CreateHumanItemAsync(host, "Dropped dependency");
        var humanAdded = await CreateHumanItemAsync(host, "Added by a human");
        await HumanAddsAsync(host, itemId, dropped);
        var proposalId = await ProposeOkAsync(host.Agent(runId: await SeedRunAsync(host.Factory)), itemId,
            new { removeDependencies = new[] { dropped } });
        await HumanAddsAsync(host, itemId, humanAdded);

        var resp = await ApproveAsync(host, itemId, proposalId);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("Stale", (await ReadJsonAsync(resp)).GetProperty("proposal").GetProperty("status").GetString());
        Assert.Equal(new[] { dropped, humanAdded }.Order(), (await host.StoredDependenciesAsync(itemId)).Order());
        Assert.Equal("Stale", await ProposalStatusAsync(host, itemId, proposalId));
    }

    [Fact]
    public async Task A_proposal_without_dependency_changes_still_applies_after_the_dependency_set_changed()
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host, "Human's item");
        var humanAdded = await CreateHumanItemAsync(host, "Added by a human");
        var proposalId = await ProposeOkAsync(host.Agent(chatSessionId: await SeedChatSessionAsync(host.Factory)), itemId,
            new { title = "Sharper title" });
        await HumanAddsAsync(host, itemId, humanAdded);

        var resp = await ApproveAsync(host, itemId, proposalId);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("Sharper title", (await GetItemAsync(host, itemId)).GetProperty("title").GetString());
        Assert.Equal(new[] { humanAdded }, await host.StoredDependenciesAsync(itemId));
    }

    [Fact]
    public async Task An_approval_that_would_now_close_a_cycle_is_refused_and_stays_pending_until_the_item_changes_and_it_goes_stale()
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host, "Human's item");
        var added = await CreateHumanItemAsync(host, "Proposed dependency");
        var proposalId = await ProposeOkAsync(host.Agent(runId: await SeedRunAsync(host.Factory)), itemId,
            new { addDependencies = new[] { added } });
        await HumanAddsAsync(host, added, itemId);

        var refused = await ApproveAsync(host, itemId, proposalId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var body = await ReadJsonAsync(refused);
        Assert.Contains("cycle", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Pending", body.GetProperty("proposal").GetProperty("status").GetString());
        Assert.Equal("Pending", await ProposalStatusAsync(host, itemId, proposalId));
        Assert.Empty(await host.StoredDependenciesAsync(itemId));

        var humanAdded = await CreateHumanItemAsync(host, "Added by a human");
        await HumanAddsAsync(host, itemId, humanAdded);

        var stale = await ApproveAsync(host, itemId, proposalId);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("Stale", await ProposalStatusAsync(host, itemId, proposalId));
        Assert.Equal(new[] { humanAdded }, await host.StoredDependenciesAsync(itemId));
    }

    [Fact]
    public async Task A_proposed_item_deleted_before_approval_reads_without_a_title_and_approving_is_refused()
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host, "Human's item");
        var kept = await CreateHumanItemAsync(host, "Kept item");
        var deleted = await CreateHumanItemAsync(host, "Soon deleted");
        var proposalId = await ProposeOkAsync(host.Agent(runId: await SeedRunAsync(host.Factory)), itemId,
            new { addDependencies = new[] { kept, deleted } });
        (await host.Human.DeleteAsync($"/api/v1/workitems/{deleted}", Ct)).EnsureSuccessStatusCode();

        var proposed = (await ReadProposalAsync(host, itemId, proposalId)).GetProperty("proposed");
        Assert.Equal(new[] { (kept, (string?)"Kept item"), (deleted, (string?)null) }, NamedItems(proposed, "addDependencies"));

        var resp = await ApproveAsync(host, itemId, proposalId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Contains(deleted, body.GetProperty("error").GetString());
        Assert.Equal("Pending", body.GetProperty("proposal").GetProperty("status").GetString());
        Assert.Equal("Pending", await ProposalStatusAsync(host, itemId, proposalId));
        Assert.Empty(await host.StoredDependenciesAsync(itemId));
    }

    public enum BadProposal
    {
        BlankAddition, BlankRemoval, UnknownAddition, UnknownRemoval, ItemItselfAdded, ItemItselfRemoved,
        AdditionRepeated, RemovalRepeated, InBothLists, AddingAnExistingDependency, RemovingANonDependency,
        DirectCycle, TransitiveCycle,
    }

    [Theory]
    [InlineData(BadProposal.BlankAddition)]
    [InlineData(BadProposal.BlankRemoval)]
    [InlineData(BadProposal.UnknownAddition)]
    [InlineData(BadProposal.UnknownRemoval)]
    [InlineData(BadProposal.ItemItselfAdded)]
    [InlineData(BadProposal.ItemItselfRemoved)]
    [InlineData(BadProposal.AdditionRepeated)]
    [InlineData(BadProposal.RemovalRepeated)]
    [InlineData(BadProposal.InBothLists)]
    [InlineData(BadProposal.AddingAnExistingDependency)]
    [InlineData(BadProposal.RemovingANonDependency)]
    [InlineData(BadProposal.DirectCycle)]
    [InlineData(BadProposal.TransitiveCycle)]
    public async Task A_dependency_proposal_that_could_never_apply_is_a_400_that_stores_nothing(BadProposal bad)
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host, "Human's item");
        var existingDep = await CreateHumanItemAsync(host, "Existing dependency");
        var free = await CreateHumanItemAsync(host, "Unrelated item");
        var waitsOnItem = await CreateHumanItemAsync(host, "Waits on the item");
        var waitsFurther = await CreateHumanItemAsync(host, "Waits further down");
        await HumanAddsAsync(host, itemId, existingDep);
        await HumanAddsAsync(host, waitsOnItem, itemId);
        await HumanAddsAsync(host, waitsFurther, waitsOnItem);
        var unknown = Guid.NewGuid().ToString();

        (string[]? Add, string[]? Remove) lists = bad switch
        {
            BadProposal.BlankAddition => (new[] { "  " }, null),
            BadProposal.BlankRemoval => (null, new[] { "" }),
            BadProposal.UnknownAddition => (new[] { unknown }, null),
            BadProposal.UnknownRemoval => (null, new[] { unknown }),
            BadProposal.ItemItselfAdded => (new[] { itemId }, null),
            BadProposal.ItemItselfRemoved => (null, new[] { itemId }),
            BadProposal.AdditionRepeated => (new[] { free, free }, null),
            BadProposal.RemovalRepeated => (null, new[] { existingDep, existingDep }),
            BadProposal.InBothLists => (new[] { free }, new[] { free }),
            BadProposal.AddingAnExistingDependency => (new[] { existingDep }, null),
            BadProposal.RemovingANonDependency => (null, new[] { free }),
            BadProposal.DirectCycle => (new[] { waitsOnItem }, null),
            _ => (new[] { waitsFurther }, null),
        };

        // A valid title alongside, so the dependency lists are the only thing to refuse.
        var resp = await ProposeAsync(host.Agent(runId: await SeedRunAsync(host.Factory)), itemId,
            new { title = "A valid title", addDependencies = lists.Add, removeDependencies = lists.Remove });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await ErrorOfAsync(resp)));
        Assert.Empty(await ListAsHumanAsync(host, itemId));
        Assert.Equal(new[] { existingDep }, await host.StoredDependenciesAsync(itemId));
    }

    [Fact]
    public async Task An_approved_removal_of_the_last_unfinished_dependency_promotes_the_same_as_the_human_route()
    {
        await using var host = await StartHostAsync();
        var blocker = await CreateHumanItemAsync(host, "Unfinished blocker");
        var proposedOn = await CreateHumanItemAsync(host, "Edited by proposal");
        var editedByHuman = await CreateHumanItemAsync(host, "Edited by a human");
        await HumanAddsAsync(host, proposedOn, blocker);
        await HumanAddsAsync(host, editedByHuman, blocker);
        await ToWorkQueueAsync(host, proposedOn);
        await ToWorkQueueAsync(host, editedByHuman);
        var proposalId = await ProposeOkAsync(host.Agent(runId: await SeedRunAsync(host.Factory)), proposedOn,
            new { removeDependencies = new[] { blocker } });

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(host, proposedOn, proposalId)).StatusCode);
        await HumanRemovesAsync(host, editedByHuman, blocker);

        Assert.Empty(await host.StoredDependenciesAsync(proposedOn));
        Assert.Equal(await StatusOfAsync(host, editedByHuman), await StatusOfAsync(host, proposedOn));

        await host.Server.Service.ReconcileWorkQueueAsync(Ct);

        Assert.Equal("Ready", await StatusOfAsync(host, proposedOn));
        Assert.Equal("Ready", await StatusOfAsync(host, editedByHuman));
    }

    // -- Seeding ------------------------------------------------------------------

    private static async Task<Guid> SeedChatSessionAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = new ChatSession
        {
            Id = Guid.NewGuid(),
            UserId = "dependency-tester",
            AiProviderId = Guid.NewGuid(),
            ProviderType = "claude-code",
            ToolAllowlistCsv = "ild",
            ScratchPath = "/tmp/ild-test-chat-session",
            CreatedAt = DateTime.UtcNow,
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private static async Task<Guid> SeedRunAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"dependencies-{Guid.NewGuid():N}" };
        db.LoopTemplates.Add(template);
        var version = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = template.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.LoopTemplateVersions.Add(version);
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid().ToString(),
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            StartedAt = DateTime.UtcNow,
        };
        db.LoopRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static async Task<Guid> SeedRepositoryAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provider = new RemoteProvider
        {
            Id = Guid.NewGuid(),
            Name = "test-provider",
            Type = "forgejo",
            Url = "https://example.invalid",
            CreatedAt = DateTime.UtcNow,
        };
        db.RemoteProviders.Add(provider);
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = "repo",
            CloneUrl = "https://example.invalid/repo.git",
            RemoteProviderId = provider.Id,
            DefaultIntakeStatus = WorkItemStatus.Backlog,
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(repo);
        await db.SaveChangesAsync();
        return repo.Id;
    }
}
