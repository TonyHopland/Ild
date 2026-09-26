using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
/// Work Item Edit Proposals through ILD's two surfaces: the agent surface may
/// propose an edit to any work item and read what became of it, and only the
/// human surface may approve or reject one. Agent calls are made the way the
/// MCP server makes them — the agent service token plus the session header.
/// </summary>
public class WorkItemEditProposalApiTests
{
    private const string RunHeader = "X-ILD-Run-Id";
    private const string ChatHeader = "X-ILD-Chat-Session-Id";

    private sealed class Host : IAsyncDisposable
    {
        public ApiFactory Factory { get; }
        public Mock<IWorkItemNotifier> WorkItemNotifier { get; } = new();
        public Mock<IChatNotifier> ChatNotifier { get; } = new();
        public HttpClient Human { get; private set; } = null!;
        public Guid RepositoryId { get; private set; }

        public Host()
        {
            Factory = new ApiFactory(configureServices: services =>
            {
                services.ReplaceSingleton(WorkItemNotifier.Object);
                services.ReplaceSingleton(ChatNotifier.Object);
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

        public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
    }

    private static async Task<Host> StartHostAsync() => await new Host().StartAsync();

    /// <summary>An older backlog item no agent session created.</summary>
    private static async Task<string> CreateHumanItemAsync(Host host)
    {
        var resp = await host.Human.PostAsJsonAsync("/api/v1/workitems", new
        {
            title = "Old backlog item",
            description = "The original description.",
            repositoryId = host.RepositoryId.ToString(),
            tags = new[] { "legacy-tag" },
            branchNameOverride = "feature/original",
            baseBranchOverride = "develop",
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await ReadJsonAsync(resp)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> GetItemAsync(Host host, string id)
    {
        var resp = await host.Human.GetAsync($"/api/v1/workitems/{id}");
        resp.EnsureSuccessStatusCode();
        return await ReadJsonAsync(resp);
    }

    private static void AssertEditableFields(JsonElement item, string title, string description, string[] tags, string? branch, string? baseBranch)
    {
        Assert.Equal(title, item.GetProperty("title").GetString());
        Assert.Equal(description, item.GetProperty("description").GetString());
        Assert.Equal(tags, item.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray());
        Assert.Equal(branch, item.GetProperty("branchNameOverride").GetString());
        Assert.Equal(baseBranch, item.GetProperty("baseBranchOverride").GetString());
    }

    private static void AssertUnchanged(JsonElement item)
        => AssertEditableFields(item, "Old backlog item", "The original description.",
            new[] { "legacy-tag" }, "feature/original", "develop");

    private static Task<HttpResponseMessage> ProposeAsync(HttpClient agent, string itemId, object body)
        => agent.PostAsJsonAsync($"/api/v1/agent/workitems/{itemId}/edit-proposals", body);

    private static Task<HttpResponseMessage> ProposeRawAsync(HttpClient agent, string itemId, string json)
        => agent.PostAsync($"/api/v1/agent/workitems/{itemId}/edit-proposals",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<string> ProposeOkAsync(HttpClient agent, string itemId, object body)
    {
        var resp = await ProposeAsync(agent, itemId, body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var json = await ReadJsonAsync(resp);
        Assert.Equal("Pending", json.GetProperty("status").GetString());
        return json.GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement[]> ListAsHumanAsync(Host host, string itemId)
    {
        var resp = await host.Human.GetAsync($"/api/v1/workitems/{itemId}/edit-proposals");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await ReadJsonAsync(resp)).EnumerateArray().ToArray();
    }

    private static async Task<JsonElement> ReadProposalAsync(Host host, string itemId, string proposalId)
        => Assert.Single(await ListAsHumanAsync(host, itemId), p => p.GetProperty("id").GetString() == proposalId);

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, string itemId, string proposalId)
        => client.PostAsync($"/api/v1/workitems/{itemId}/edit-proposals/{proposalId}/approve", null);

    private static Task<HttpResponseMessage> RejectAsync(HttpClient client, string itemId, string proposalId, string? reason)
        => client.PostAsJsonAsync($"/api/v1/workitems/{itemId}/edit-proposals/{proposalId}/reject", new { reason });

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
        => JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

    private static bool IsNullOrAbsent(JsonElement obj, string property)
        => !obj.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null;

    public enum Caller { LoopRun, ChatSession, Both }

    [Theory]
    [InlineData(Caller.LoopRun)]
    [InlineData(Caller.ChatSession)]
    [InlineData(Caller.Both)]
    public async Task An_agent_proposes_an_edit_to_an_item_it_did_not_create_and_the_item_is_unchanged(Caller caller)
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host);
        var runId = caller is Caller.LoopRun or Caller.Both ? await SeedRunAsync(host.Factory) : (Guid?)null;
        var chatId = caller is Caller.ChatSession or Caller.Both ? await SeedChatSessionAsync(host.Factory) : (Guid?)null;

        var proposalId = await ProposeOkAsync(host.Agent(runId, chatId), itemId, new
        {
            description = "A sharper description.",
            rationale = "The old description no longer matches the code.",
        });

        var item = await GetItemAsync(host, itemId);
        AssertUnchanged(item);
        Assert.Equal(1, item.GetProperty("pendingEditProposalCount").GetInt32());

        var proposal = await ReadProposalAsync(host, itemId, proposalId);
        Assert.Equal("Pending", proposal.GetProperty("status").GetString());
        Assert.Equal(itemId, proposal.GetProperty("workItemId").GetString());
        Assert.Equal("The old description no longer matches the code.", proposal.GetProperty("rationale").GetString());
        if (runId is { } run)
        {
            Assert.Equal(run.ToString(), proposal.GetProperty("createdByLoopRunId").GetString());
            Assert.True(IsNullOrAbsent(proposal, "createdByChatSessionId"));
        }
        else
        {
            Assert.Equal(chatId.ToString(), proposal.GetProperty("createdByChatSessionId").GetString());
            Assert.True(IsNullOrAbsent(proposal, "createdByLoopRunId"));
        }

        var proposed = proposal.GetProperty("proposed");
        Assert.Equal("A sharper description.", proposed.GetProperty("description").GetString());
        foreach (var notProposed in new[] { "title", "tags", "branchNameOverride", "baseBranchOverride" })
            Assert.True(IsNullOrAbsent(proposed, notProposed), $"{notProposed} was not proposed");

        var snapshot = proposal.GetProperty("snapshot");
        AssertEditableFields(snapshot, "Old backlog item", "The original description.",
            new[] { "legacy-tag" }, "feature/original", "develop");
    }

    [Fact]
    public async Task Proposing_on_an_item_the_callers_own_session_created_is_still_only_a_proposal()
    {
        await using var host = await StartHostAsync();
        var runId = await SeedRunAsync(host.Factory);
        var agent = host.Agent(runId);
        var created = await agent.PostAsJsonAsync("/api/v1/agent/workitems", new
        {
            title = "Agent's own item",
            description = "",
            repositoryId = host.RepositoryId.ToString(),
        });
        created.EnsureSuccessStatusCode();
        var itemId = (await ReadJsonAsync(created)).GetProperty("id").GetString()!;

        await ProposeOkAsync(agent, itemId, new { title = "Renamed" });

        Assert.Equal("Agent's own item", (await GetItemAsync(host, itemId)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_tag_outside_the_loop_templates_and_a_blank_override_are_proposable()
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host);
        var agent = host.Agent(await SeedRunAsync(host.Factory));

        var proposalId = await ProposeOkAsync(agent, itemId, new { tags = new[] { "HIL" }, branchNameOverride = "" });

        var proposed = (await ReadProposalAsync(host, itemId, proposalId)).GetProperty("proposed");
        Assert.Equal(new[] { "HIL" }, proposed.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray());
        Assert.Equal("", proposed.GetProperty("branchNameOverride").GetString());
        Assert.True(IsNullOrAbsent(proposed, "baseBranchOverride"));
    }

    public static TheoryData<string, string> InvalidProposals => new()
    {
        { "nothing proposed", "{}" },
        { "only a rationale", """{"rationale":"just because"}""" },
        { "empty title", """{"title":""}""" },
        { "blank title", """{"title":"   "}""" },
        { "title over 512", $$"""{"title":"{{new string('t', 513)}}"}""" },
        { "illegal branch name", """{"branchNameOverride":"bad..name"}""" },
        { "illegal base branch", """{"baseBranchOverride":"has space"}""" },
        { "rationale over 2000", $$"""{"title":"ok","rationale":"{{new string('r', 2001)}}"}""" },
    };

    [Theory]
    [MemberData(nameof(InvalidProposals))]
    public async Task An_invalid_proposal_is_a_400_that_stores_nothing(string _, string json)
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host);
        var agent = host.Agent(await SeedRunAsync(host.Factory));

        var resp = await ProposeRawAsync(agent, itemId, json);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(await ListAsHumanAsync(host, itemId));
    }

    public enum SessionHeader { None, Unparseable, UnknownRun, UnknownChat }

    [Theory]
    [InlineData(SessionHeader.None, HttpStatusCode.BadRequest)]
    [InlineData(SessionHeader.Unparseable, HttpStatusCode.BadRequest)]
    [InlineData(SessionHeader.UnknownRun, HttpStatusCode.Forbidden)]
    [InlineData(SessionHeader.UnknownChat, HttpStatusCode.Forbidden)]
    public async Task A_proposal_needs_a_session_that_exists(SessionHeader header, HttpStatusCode expected)
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host);
        var agent = host.Agent(
            runId: header == SessionHeader.UnknownRun ? Guid.NewGuid() : null,
            chatSessionId: header == SessionHeader.UnknownChat ? Guid.NewGuid() : null);
        if (header == SessionHeader.Unparseable)
            agent.DefaultRequestHeaders.Add(RunHeader, "not-a-guid");

        var resp = await ProposeAsync(agent, itemId, new { title = "Renamed" });

        Assert.Equal(expected, resp.StatusCode);
        Assert.Empty(await ListAsHumanAsync(host, itemId));
    }

    [Fact]
    public async Task Proposing_on_an_unknown_item_is_404()
    {
        await using var host = await StartHostAsync();
        var agent = host.Agent(await SeedRunAsync(host.Factory));

        var resp = await ProposeAsync(agent, Guid.NewGuid().ToString(), new { title = "Renamed" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task A_twenty_first_pending_proposal_on_one_item_is_refused_with_409()
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host);
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        for (var i = 0; i < 20; i++)
            await ProposeOkAsync(agent, itemId, new { title = $"Title {i}" });

        var resp = await ProposeAsync(agent, itemId, new { title = "One too many" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal(20, (await ListAsHumanAsync(host, itemId)).Length);
    }

    [Fact]
    public async Task A_human_approving_applies_exactly_the_proposed_fields_and_tells_the_board_and_the_chat()
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host);
        var statusBefore = (await GetItemAsync(host, itemId)).GetProperty("status").GetString();
        var chatId = await SeedChatSessionAsync(host.Factory);
        var proposalId = await ProposeOkAsync(host.Agent(chatSessionId: chatId), itemId,
            new { title = "Sharper title", branchNameOverride = "" });
        host.WorkItemNotifier.Invocations.Clear();
        host.ChatNotifier.Invocations.Clear();

        var resp = await ApproveAsync(host.Human, itemId, proposalId);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Equal("Approved", body.GetProperty("proposal").GetProperty("status").GetString());
        Assert.False(IsNullOrAbsent(body.GetProperty("proposal"), "decidedAt"));
        Assert.Equal("Sharper title", body.GetProperty("workItem").GetProperty("title").GetString());

        var item = await GetItemAsync(host, itemId);
        AssertEditableFields(item, "Sharper title", "The original description.", new[] { "legacy-tag" }, null, "develop");
        Assert.Equal(statusBefore, item.GetProperty("status").GetString());
        Assert.Equal(0, item.GetProperty("pendingEditProposalCount").GetInt32());

        host.WorkItemNotifier.Verify(n => n.WorkItemStateChangedAsync(
            itemId, It.IsAny<RemoteWorkItemStatus>(), It.IsAny<RemoteWorkItemStatus>()), Times.AtLeastOnce);
        host.WorkItemNotifier.Verify(n => n.WorkItemEditProposalsChangedAsync(itemId), Times.AtLeastOnce);
        host.ChatNotifier.Verify(n => n.EditProposalsChangedAsync(chatId), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Approving_after_a_human_edit_is_a_409_that_marks_the_proposal_stale_and_keeps_the_edit()
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host);
        var proposalId = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), itemId, new { title = "Agent title" });
        var edit = await host.Human.PutAsJsonAsync($"/api/v1/workitems/{itemId}", new
        {
            title = "Old backlog item",
            description = "Edited by a human.",
            repositoryId = host.RepositoryId.ToString(),
            tags = new[] { "legacy-tag" },
        });
        edit.EnsureSuccessStatusCode();

        var resp = await ApproveAsync(host.Human, itemId, proposalId);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
        Assert.Equal("Stale", body.GetProperty("proposal").GetProperty("status").GetString());
        AssertEditableFields(await GetItemAsync(host, itemId), "Old backlog item", "Edited by a human.",
            new[] { "legacy-tag" }, "feature/original", "develop");
        Assert.Equal("Stale", (await ReadProposalAsync(host, itemId, proposalId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task After_one_of_several_is_approved_the_others_are_stale_and_approving_them_is_a_409()
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host);
        var agent = host.Agent(await SeedRunAsync(host.Factory));
        var first = await ProposeOkAsync(agent, itemId, new { title = "First title" });
        var second = await ProposeOkAsync(agent, itemId, new { description = "Second description." });
        Assert.Equal(2, (await GetItemAsync(host, itemId)).GetProperty("pendingEditProposalCount").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(host.Human, itemId, first)).StatusCode);
        Assert.Equal("Stale", (await ReadProposalAsync(host, itemId, second)).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await ApproveAsync(host.Human, itemId, second)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ApproveAsync(host.Human, itemId, first)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await RejectAsync(host.Human, itemId, first, null)).StatusCode);
        AssertEditableFields(await GetItemAsync(host, itemId), "First title", "The original description.",
            new[] { "legacy-tag" }, "feature/original", "develop");
    }

    [Fact]
    public async Task Rejecting_applies_nothing_and_the_agent_can_read_the_reason()
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host);
        var chatId = await SeedChatSessionAsync(host.Factory);
        var agent = host.Agent(chatSessionId: chatId);
        var proposalId = await ProposeOkAsync(agent, itemId, new { title = "Agent title" });
        host.WorkItemNotifier.Invocations.Clear();
        host.ChatNotifier.Invocations.Clear();

        var resp = await RejectAsync(host.Human, itemId, proposalId, "Too vague.");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await ReadJsonAsync(resp);
        Assert.Equal("Rejected", body.GetProperty("status").GetString());
        Assert.Equal("Too vague.", body.GetProperty("rejectionReason").GetString());
        AssertUnchanged(await GetItemAsync(host, itemId));
        host.WorkItemNotifier.Verify(n => n.WorkItemEditProposalsChangedAsync(itemId), Times.AtLeastOnce);
        host.ChatNotifier.Verify(n => n.EditProposalsChangedAsync(chatId), Times.AtLeastOnce);

        var agentRead = await agent.GetAsync($"/api/v1/agent/workitems/{itemId}/edit-proposals");
        Assert.Equal(HttpStatusCode.OK, agentRead.StatusCode);
        var seen = Assert.Single((await ReadJsonAsync(agentRead)).EnumerateArray());
        Assert.Equal(proposalId, seen.GetProperty("id").GetString());
        Assert.Equal("Rejected", seen.GetProperty("status").GetString());
        Assert.Equal("Too vague.", seen.GetProperty("rejectionReason").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await ApproveAsync(host.Human, itemId, proposalId)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await RejectAsync(host.Human, itemId, proposalId, "again")).StatusCode);
        AssertUnchanged(await GetItemAsync(host, itemId));
    }

    [Fact]
    public async Task A_reject_reason_over_2000_characters_is_refused_and_the_proposal_stays_pending()
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host);
        var proposalId = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), itemId, new { title = "Agent title" });

        var resp = await RejectAsync(host.Human, itemId, proposalId, new string('x', 2001));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("Pending", (await ReadProposalAsync(host, itemId, proposalId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_unknown_proposal_or_one_of_another_item_is_404()
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host);
        var otherId = await CreateHumanItemAsync(host);
        var proposalId = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), itemId, new { title = "Agent title" });

        Assert.Equal(HttpStatusCode.NotFound, (await ApproveAsync(host.Human, itemId, Guid.NewGuid().ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApproveAsync(host.Human, otherId, proposalId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RejectAsync(host.Human, otherId, proposalId, null)).StatusCode);
        Assert.Equal("Pending", (await ReadProposalAsync(host, itemId, proposalId)).GetProperty("status").GetString());
        AssertUnchanged(await GetItemAsync(host, otherId));
    }

    [Fact]
    public async Task Creating_a_proposal_from_a_chat_hints_the_item_and_the_chat()
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host);
        var chatId = await SeedChatSessionAsync(host.Factory);

        await ProposeOkAsync(host.Agent(chatSessionId: chatId), itemId, new { title = "Agent title" });

        host.WorkItemNotifier.Verify(n => n.WorkItemEditProposalsChangedAsync(itemId), Times.AtLeastOnce);
        host.ChatNotifier.Verify(n => n.EditProposalsChangedAsync(chatId), Times.AtLeastOnce);
    }

    [Fact]
    public async Task The_human_can_list_pending_proposals_across_items_and_a_chats_proposals()
    {
        await using var host = await StartHostAsync();
        var itemA = await CreateHumanItemAsync(host);
        var itemB = await CreateHumanItemAsync(host);
        var chatId = await SeedChatSessionAsync(host.Factory);
        var fromChat = await ProposeOkAsync(host.Agent(chatSessionId: chatId), itemA, new { title = "From chat" });
        var fromRun = await ProposeOkAsync(host.Agent(await SeedRunAsync(host.Factory)), itemB, new { title = "From run" });
        var decided = await ProposeOkAsync(host.Agent(chatSessionId: chatId), itemB, new { description = "Decided" });
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(host.Human, itemB, decided, null)).StatusCode);

        var pending = await host.Human.GetAsync("/api/v1/workitems/edit-proposals?status=Pending");
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        Assert.Equal(new[] { fromChat, fromRun }.Order(),
            (await ReadJsonAsync(pending)).EnumerateArray().Select(p => p.GetProperty("id").GetString()!).Order());

        var chats = await host.Human.GetAsync($"/api/v1/workitems/edit-proposals?chatSessionId={chatId}");
        Assert.Equal(HttpStatusCode.OK, chats.StatusCode);
        Assert.Equal(new[] { fromChat, decided }.Order(),
            (await ReadJsonAsync(chats)).EnumerateArray().Select(p => p.GetProperty("id").GetString()!).Order());
    }

    [Fact]
    public async Task The_agent_token_cannot_list_approve_or_reject_on_the_human_surface()
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host);
        var runId = await SeedRunAsync(host.Factory);
        var agent = host.Agent(runId);
        var proposalId = await ProposeOkAsync(agent, itemId, new { title = "Agent title" });

        Assert.Equal(HttpStatusCode.Forbidden, (await ApproveAsync(agent, itemId, proposalId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await RejectAsync(agent, itemId, proposalId, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync($"/api/v1/workitems/{itemId}/edit-proposals")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/v1/workitems/edit-proposals?status=Pending")).StatusCode);

        foreach (var path in new[]
        {
            $"/api/v1/agent/workitems/{itemId}/edit-proposals/{proposalId}/approve",
            $"/api/v1/agent/workitems/{itemId}/edit-proposals/{proposalId}/reject",
            $"/api/v1/agent/edit-proposals/{proposalId}/approve",
            $"/api/v1/agent/edit-proposals/{proposalId}/reject",
        })
        {
            var resp = await agent.PostAsJsonAsync(path, new { reason = "self-approved" });
            Assert.False(resp.IsSuccessStatusCode, $"{path} answered {(int)resp.StatusCode}");
        }

        Assert.Equal("Pending", (await ReadProposalAsync(host, itemId, proposalId)).GetProperty("status").GetString());
        AssertUnchanged(await GetItemAsync(host, itemId));
    }

    [Fact]
    public async Task Update_workitem_still_refuses_an_item_the_session_did_not_create_and_points_at_proposing()
    {
        await using var host = await StartHostAsync();
        var itemId = await CreateHumanItemAsync(host);
        var agent = host.Agent(await SeedRunAsync(host.Factory));

        var update = await agent.PutAsJsonAsync($"/api/v1/agent/workitems/{itemId}", new { title = "Direct edit", description = "" });
        var delete = await agent.DeleteAsync($"/api/v1/agent/workitems/{itemId}");

        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Contains("propose_workitem_edit", (await ReadJsonAsync(update)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        AssertUnchanged(await GetItemAsync(host, itemId));
    }

    private static async Task<Guid> SeedChatSessionAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = new ChatSession
        {
            Id = Guid.NewGuid(),
            UserId = "edit-proposal-tester",
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
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"proposals-{Guid.NewGuid():N}" };
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
