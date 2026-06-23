using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// Integration tests for the agent-scoped API surface (<c>/api/v1/agent/...</c>)
/// consumed by the ILD MCP server. Two contracts matter most:
///   - items created via this surface MUST land in Backlog regardless of the
///     repository's <c>DefaultIntakeStatus</c>, because agents may not start work,
///   - items MUST be stamped with the originating LoopRun id (from header or
///     body) so a user can batch-clean up rogue agent output.
/// </summary>
[Collection("AuthEnvironment")]
public class AgentApiIntegrationTests
{
    [Fact]
    public async Task CreateWorkItem_forces_backlog_even_when_repo_default_is_workqueue()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var repoId = await SeedRepositoryAsync(factory, intake: WorkItemStatus.WorkQueue);

        var resp = await client.PostAsJsonAsync("/api/v1/agent/workitems", new
        {
            title = "Agent-created",
            description = "from MCP",
            repositoryId = repoId.ToString(),
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("Backlog", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreateWorkItem_stamps_run_id_from_header()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var repoId = await SeedRepositoryAsync(factory, intake: WorkItemStatus.Backlog);
        var runId = Guid.NewGuid();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/workitems")
        {
            Content = JsonContent.Create(new { title = "stamped", description = "", repositoryId = repoId.ToString() })
        };
        req.Headers.Add("X-ILD-Run-Id", runId.ToString());

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(runId.ToString(), doc.RootElement.GetProperty("createdByLoopRunId").GetString());
    }

    [Fact]
    public async Task CreateWorkItem_stamps_chat_session_id_from_header()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var repoId = await SeedRepositoryAsync(factory, intake: WorkItemStatus.WorkQueue);
        var chatSessionId = Guid.NewGuid();

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/workitems")
        {
            Content = JsonContent.Create(new { title = "from chat", description = "", repositoryId = repoId.ToString() })
        };
        req.Headers.Add("X-ILD-Chat-Session-Id", chatSessionId.ToString());

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        // Chat-created items carry the chat stamp (not a run stamp) and still land in Backlog.
        Assert.Equal(chatSessionId.ToString(), doc.RootElement.GetProperty("createdByChatSessionId").GetString());
        Assert.Equal("Backlog", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.GetProperty("createdByLoopRunId").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task ListWorkItems_filters_by_createdByLoopRunId()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var repoId = await SeedRepositoryAsync(factory, intake: WorkItemStatus.Backlog);
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();

        await CreateAsync(client, "from A 1", runA, repoId);
        await CreateAsync(client, "from A 2", runA, repoId);
        await CreateAsync(client, "from B",   runB, repoId);

        var resp = await client.GetAsync($"/api/v1/agent/workitems?createdByLoopRunId={runA}");
        resp.EnsureSuccessStatusCode();
        var arr = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, arr.GetArrayLength());
    }

    [Fact]
    public async Task UpdateWorkItem_succeeds_for_item_created_by_the_same_run()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var repoId = await SeedRepositoryAsync(factory, intake: WorkItemStatus.Backlog);
        var runId = Guid.NewGuid();

        var itemId = await CreateAsync(client, "original", runId, repoId);

        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent/workitems/{itemId}")
        {
            Content = JsonContent.Create(new { title = "edited", description = "new body" }),
        };
        put.Headers.Add("X-ILD-Run-Id", runId.ToString());
        var resp = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("edited", doc.GetProperty("title").GetString());
        Assert.Equal("new body", doc.GetProperty("description").GetString());
    }

    [Fact]
    public async Task UpdateWorkItem_is_forbidden_for_item_created_by_a_different_run()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var repoId = await SeedRepositoryAsync(factory, intake: WorkItemStatus.Backlog);
        var ownerRun = Guid.NewGuid();
        var otherRun = Guid.NewGuid();

        var itemId = await CreateAsync(client, "owned by A", ownerRun, repoId);

        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent/workitems/{itemId}")
        {
            Content = JsonContent.Create(new { title = "hijacked", description = "" }),
        };
        put.Headers.Add("X-ILD-Run-Id", otherRun.ToString());
        var resp = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        // The item is untouched.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent/workitems/{itemId}");
        Assert.Equal("owned by A", detail.GetProperty("title").GetString());
    }

    [Fact]
    public async Task UpdateWorkItem_without_a_session_header_is_forbidden()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var repoId = await SeedRepositoryAsync(factory, intake: WorkItemStatus.Backlog);
        var runId = Guid.NewGuid();

        var itemId = await CreateAsync(client, "owned", runId, repoId);

        var resp = await client.PutAsJsonAsync(
            $"/api/v1/agent/workitems/{itemId}",
            new { title = "anon edit", description = "" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task UpdateWorkItem_for_unknown_item_returns_not_found()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent/workitems/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new { title = "ghost", description = "" }),
        };
        put.Headers.Add("X-ILD-Run-Id", Guid.NewGuid().ToString());
        var resp = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task DeleteWorkItem_succeeds_for_item_created_by_the_same_run()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var repoId = await SeedRepositoryAsync(factory, intake: WorkItemStatus.Backlog);
        var runId = Guid.NewGuid();

        var itemId = await CreateAsync(client, "disposable", runId, repoId);

        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/agent/workitems/{itemId}");
        del.Headers.Add("X-ILD-Run-Id", runId.ToString());
        var resp = await client.SendAsync(del);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var detail = await client.GetAsync($"/api/v1/agent/workitems/{itemId}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [Fact]
    public async Task DeleteWorkItem_is_forbidden_for_item_created_by_a_different_run()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var repoId = await SeedRepositoryAsync(factory, intake: WorkItemStatus.Backlog);
        var ownerRun = Guid.NewGuid();
        var otherRun = Guid.NewGuid();

        var itemId = await CreateAsync(client, "protected", ownerRun, repoId);

        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/agent/workitems/{itemId}");
        del.Headers.Add("X-ILD-Run-Id", otherRun.ToString());
        var resp = await client.SendAsync(del);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        // Still there.
        var detail = await client.GetAsync($"/api/v1/agent/workitems/{itemId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    [Fact]
    public async Task DeleteWorkItem_is_scoped_to_the_creating_chat_session()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var repoId = await SeedRepositoryAsync(factory, intake: WorkItemStatus.Backlog);
        var chatSessionId = Guid.NewGuid();

        // Create via a chat session (chat header, no run header).
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/workitems")
        {
            Content = JsonContent.Create(new { title = "from chat", description = "", repositoryId = repoId.ToString() }),
        };
        create.Headers.Add("X-ILD-Chat-Session-Id", chatSessionId.ToString());
        var createResp = await client.SendAsync(create);
        createResp.EnsureSuccessStatusCode();
        var itemId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString();

        // A different chat session may not delete it.
        var forbidden = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/agent/workitems/{itemId}");
        forbidden.Headers.Add("X-ILD-Chat-Session-Id", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(forbidden)).StatusCode);

        // The creating chat session may.
        var allowed = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/agent/workitems/{itemId}");
        allowed.Headers.Add("X-ILD-Chat-Session-Id", chatSessionId.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(allowed)).StatusCode);
    }

    [Fact]
    public async Task UpdateWorkItem_is_scoped_to_the_creating_chat_session()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var repoId = await SeedRepositoryAsync(factory, intake: WorkItemStatus.Backlog);
        var chatSessionId = Guid.NewGuid();

        // Create via a chat session (chat header, no run header).
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/workitems")
        {
            Content = JsonContent.Create(new { title = "chat original", description = "", repositoryId = repoId.ToString() }),
        };
        create.Headers.Add("X-ILD-Chat-Session-Id", chatSessionId.ToString());
        var createResp = await client.SendAsync(create);
        createResp.EnsureSuccessStatusCode();
        var itemId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString();

        // A different chat session may not edit it.
        var forbidden = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent/workitems/{itemId}")
        {
            Content = JsonContent.Create(new { title = "hijacked", description = "" }),
        };
        forbidden.Headers.Add("X-ILD-Chat-Session-Id", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(forbidden)).StatusCode);

        // The creating chat session may.
        var allowed = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent/workitems/{itemId}")
        {
            Content = JsonContent.Create(new { title = "chat edited", description = "by chat" }),
        };
        allowed.Headers.Add("X-ILD-Chat-Session-Id", chatSessionId.ToString());
        var resp = await client.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("chat edited", doc.GetProperty("title").GetString());
    }

    [Fact]
    public async Task CreateWorkItem_rejects_unknown_dependency()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var repoId = await SeedRepositoryAsync(factory, intake: WorkItemStatus.Backlog);

        var resp = await client.PostAsJsonAsync("/api/v1/agent/workitems", new
        {
            title = "bad-dep",
            description = "",
            repositoryId = repoId.ToString(),
            dependencies = new[] { Guid.NewGuid().ToString() },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreateWorkItem_attaches_known_dependency()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var repoId = await SeedRepositoryAsync(factory, intake: WorkItemStatus.Backlog);

        var depId = await CreateAsync(client, "dep", null, repoId);
        var resp = await client.PostAsJsonAsync("/api/v1/agent/workitems", new
        {
            title = "child",
            description = "",
            repositoryId = repoId.ToString(),
            dependencies = new[] { depId.ToString() },
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var newId = doc.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(newId));

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/agent/workitems/{newId}");
        Assert.Equal(1, detail.GetProperty("dependencies").GetArrayLength());
    }

    [Fact]
    public async Task Agent_surface_does_not_expose_start_or_transition()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var startResp = await client.PostAsync("/api/v1/agent/workitems/" + Guid.NewGuid() + "/start", null);
        Assert.Equal(HttpStatusCode.NotFound, startResp.StatusCode);

        var trResp = await client.PostAsJsonAsync(
            "/api/v1/agent/workitems/" + Guid.NewGuid() + "/transition",
            new { targetStatus = "Ready" });
        Assert.Equal(HttpStatusCode.NotFound, trResp.StatusCode);
    }

    [Fact]
    public async Task SetVariable_then_ListVariables_round_trips_for_the_run()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var runId = await SeedRunAsync(factory);

        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agent/variables/handoff")
        {
            Content = JsonContent.Create(new { value = "ready for review" }),
        };
        put.Headers.Add("X-ILD-Run-Id", runId.ToString());
        var putResp = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/variables");
        get.Headers.Add("X-ILD-Run-Id", runId.ToString());
        var getResp = await client.SendAsync(get);
        getResp.EnsureSuccessStatusCode();

        var arr = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("handoff", arr[0].GetProperty("name").GetString());
        Assert.Equal("ready for review", arr[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task SetVariable_overwrites_an_existing_value()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var runId = await SeedRunAsync(factory);

        await PutVariableAsync(client, runId, "summary", "draft");
        await PutVariableAsync(client, runId, "summary", "final");

        var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/variables");
        get.Headers.Add("X-ILD-Run-Id", runId.ToString());
        var arr = JsonDocument.Parse(await (await client.SendAsync(get)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, arr.GetArrayLength());
        Assert.Equal("final", arr[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task SetVariable_rejects_an_illegal_name()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var runId = await SeedRunAsync(factory);

        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agent/variables/bad-name")
        {
            Content = JsonContent.Create(new { value = "x" }),
        };
        put.Headers.Add("X-ILD-Run-Id", runId.ToString());
        var resp = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SetVariable_rejects_a_name_longer_than_the_column_bound()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var runId = await SeedRunAsync(factory);

        // 129 chars: pattern-shaped but one over the 128-char Name column, so it
        // must surface as a clean 400 rather than overflowing the column.
        var tooLong = "a" + new string('b', 128);
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent/variables/{tooLong}")
        {
            Content = JsonContent.Create(new { value = "x" }),
        };
        put.Headers.Add("X-ILD-Run-Id", runId.ToString());
        var resp = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SetVariable_for_unknown_run_returns_not_found()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agent/variables/handoff")
        {
            Content = JsonContent.Create(new { value = "x" }),
        };
        put.Headers.Add("X-ILD-Run-Id", Guid.NewGuid().ToString());
        var resp = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ListVariables_without_run_header_is_rejected()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var resp = await client.GetAsync("/api/v1/agent/variables");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetPreview_for_unknown_workitem_returns_not_found()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var resp = await client.GetAsync($"/api/v1/agent/workitems/{Guid.NewGuid()}/preview");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetPreview_for_workitem_without_a_worktree_returns_bad_request()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var repoId = await SeedRepositoryAsync(factory, intake: WorkItemStatus.Backlog);

        // A freshly-created agent work item has no run and therefore no worktree,
        // so the preview surface refuses it the same way the human controller does.
        var itemId = await CreateAsync(client, "no worktree", null, repoId);

        var resp = await client.GetAsync($"/api/v1/agent/workitems/{itemId}/preview");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ListLoopRuns_includes_cost_and_token_totals_per_run()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var (runId, workItemId) = await SeedRunWithUsageAsync(factory);

        var resp = await client.GetAsync($"/api/v1/agent/loop-runs?workItemId={workItemId}");
        resp.EnsureSuccessStatusCode();
        var arr = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(1, arr.GetArrayLength());
        var run = arr[0];
        Assert.Equal(runId.ToString(), run.GetProperty("id").GetString());
        // Two nodes contributed 0.25 + 0.75 USD and 30 + 70 = 100 input / 200 output tokens.
        Assert.Equal(1.0m, run.GetProperty("costUsd").GetDecimal());
        Assert.Equal(100, run.GetProperty("inputTokens").GetInt64());
        Assert.Equal(200, run.GetProperty("outputTokens").GetInt64());
    }

    [Fact]
    public async Task GetCurrentLoop_returns_the_session_scratchpad_document()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var chatSessionId = await SeedChatSessionAsync(factory);

        const string document = "{\"$schema\":\"ild-loop-template/v1\",\"name\":\"Live Loop\",\"nodes\":[]}";
        factory.Services.GetRequiredService<IChatLoopScratchpad>().Set(chatSessionId, document);

        var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/current-loop");
        get.Headers.Add("X-ILD-Chat-Session-Id", chatSessionId.ToString());
        var resp = await client.SendAsync(get);
        resp.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ild-loop-template/v1", doc.GetProperty("$schema").GetString());
        Assert.Equal("Live Loop", doc.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetCurrentLoop_reports_not_open_when_no_loop_is_stashed()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var chatSessionId = await SeedChatSessionAsync(factory);

        var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/current-loop");
        get.Headers.Add("X-ILD-Chat-Session-Id", chatSessionId.ToString());
        var resp = await client.SendAsync(get);
        resp.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.False(doc.GetProperty("loopEditorOpen").GetBoolean());
    }

    [Fact]
    public async Task GetCurrentLoop_without_a_chat_session_header_is_rejected()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var resp = await client.GetAsync("/api/v1/agent/current-loop");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CurrentLoop_for_an_unknown_chat_session_is_forbidden()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        // A header GUID that names no chat session is not the caller's to act on.
        var unknown = Guid.NewGuid().ToString();

        var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/current-loop");
        get.Headers.Add("X-ILD-Chat-Session-Id", unknown);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(get)).StatusCode);

        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agent/current-loop")
        {
            Content = JsonContent.Create(new { document = "{\"$schema\":\"ild-loop-template/v1\",\"name\":\"x\",\"nodes\":[]}" }),
        };
        put.Headers.Add("X-ILD-Chat-Session-Id", unknown);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(put)).StatusCode);
    }

    [Fact]
    public async Task UpdateCurrentLoop_accepts_a_document_for_the_chat_session()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var chatSessionId = await SeedChatSessionAsync(factory);

        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agent/current-loop")
        {
            Content = JsonContent.Create(new
            {
                document = "{\"$schema\":\"ild-loop-template/v1\",\"name\":\"Edited\",\"nodes\":[]}",
            }),
        };
        put.Headers.Add("X-ILD-Chat-Session-Id", chatSessionId.ToString());
        var resp = await client.SendAsync(put);

        // Fire-and-forget: the push is best-effort (no editor connected here), so the
        // endpoint only acknowledges acceptance.
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Fact]
    public async Task UpdateCurrentLoop_rejects_an_empty_document()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var chatSessionId = await SeedChatSessionAsync(factory);

        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agent/current-loop")
        {
            Content = JsonContent.Create(new { document = "" }),
        };
        put.Headers.Add("X-ILD-Chat-Session-Id", chatSessionId.ToString());
        var resp = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task UpdateCurrentLoop_rejects_an_oversized_document()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var chatSessionId = await SeedChatSessionAsync(factory);

        // Just over the 1 MB server-side bound.
        var huge = new string('x', 1_000_001);
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/agent/current-loop")
        {
            Content = JsonContent.Create(new { document = huge }),
        };
        put.Headers.Add("X-ILD-Chat-Session-Id", chatSessionId.ToString());
        var resp = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static async Task<(Guid RunId, string WorkItemId)> SeedRunWithUsageAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"cost-{Guid.NewGuid():N}" };
        db.LoopTemplates.Add(template);
        var version = new LoopTemplateVersion
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = template.Id,
            VersionNumber = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.LoopTemplateVersions.Add(version);
        var node = new LoopNode
        {
            Id = Guid.NewGuid(),
            LoopTemplateVersionId = version.Id,
            NodeType = NodeType.AI,
            Label = "ai",
            CreatedAt = DateTime.UtcNow,
        };
        db.LoopNodes.Add(node);
        var workItemId = Guid.NewGuid().ToString();
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            StartedAt = DateTime.UtcNow,
        };
        db.LoopRuns.Add(run);
        db.LoopRunNodes.Add(new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            LoopNodeId = node.Id,
            Status = LoopRunNodeStatus.Succeeded,
            InputTokens = 30,
            OutputTokens = 70,
            CostUsd = 0.25m,
        });
        db.LoopRunNodes.Add(new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = run.Id,
            LoopNodeId = node.Id,
            Status = LoopRunNodeStatus.Succeeded,
            InputTokens = 70,
            OutputTokens = 130,
            CostUsd = 0.75m,
        });
        await db.SaveChangesAsync();
        return (run.Id, workItemId);
    }

    private static async Task PutVariableAsync(HttpClient client, Guid runId, string name, string value)
    {
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/agent/variables/{name}")
        {
            Content = JsonContent.Create(new { value }),
        };
        put.Headers.Add("X-ILD-Run-Id", runId.ToString());
        (await client.SendAsync(put)).EnsureSuccessStatusCode();
    }

    private static async Task<Guid> SeedChatSessionAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = new ChatSession
        {
            Id = Guid.NewGuid(),
            UserId = "loop-editor-tester",
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
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "vars-template" };
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

    private static async Task<string> CreateAsync(HttpClient client, string title, Guid? runId, Guid repositoryId)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/workitems")
        {
            Content = JsonContent.Create(new { title, description = "", repositoryId = repositoryId.ToString() })
        };
        if (runId.HasValue)
            req.Headers.Add("X-ILD-Run-Id", runId.Value.ToString());
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Created work item response did not include an id.");
    }

    private static async Task<Guid> SeedRepositoryAsync(ApiFactory factory, WorkItemStatus intake)
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
            DefaultIntakeStatus = intake,
            CreatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(repo);
        await db.SaveChangesAsync();
        return repo.Id;
    }
}
