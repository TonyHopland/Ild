using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Web;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.McpServer;
using ILD.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using ServerActor = ILD.WorkItemServer.Domain.WorkItemActor;
using WorkItemActorKind = ILD.WorkItemServer.Domain.WorkItemActorKind;
using WorkItemHistoryEntry = ILD.WorkItemServer.Domain.WorkItemHistoryEntry;
using WorkItemHistoryKind = ILD.WorkItemServer.Domain.WorkItemHistoryKind;

namespace ILD.Tests.Integration;

/// <summary>
/// Work item history through ILD: who ILD says made each change (the signed-in
/// human, a loop run with the item it works on, a chat, the human approving a
/// proposal), the user and agent routes that read it, and the MCP tool option
/// that asks for it.
/// </summary>
public class WorkItemHistoryApiTests : IClassFixture<WorkItemHistoryApiTests.Host>
{
    private const string RunHeader = "X-ILD-Run-Id";
    private const string ChatHeader = "X-ILD-Chat-Session-Id";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Host _host;

    public WorkItemHistoryApiTests(Host host) => _host = host;

    public sealed class Host : IAsyncLifetime
    {
        public ApiFactory Factory { get; }
        public HttpClient Human { get; private set; } = null!;
        public Guid RepositoryId { get; private set; }

        public Host()
            : this(null)
        {
        }

        internal Host(Action<IServiceCollection>? configureServices)
        {
            Factory = new ApiFactory(configureServices: services =>
            {
                services.ReplaceSingleton(new Mock<IWorkItemNotifier>().Object);
                services.ReplaceSingleton(new Mock<IChatNotifier>().Object);
                configureServices?.Invoke(services);
            });
        }

        public async ValueTask InitializeAsync() => await StartAsync();

        internal async Task<Host> StartAsync()
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

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
        => JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct)).RootElement;

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    private static async Task<string> CreateAsHumanAsync(Host host, string title)
    {
        var resp = await host.Human.PostAsJsonAsync("/api/v1/workitems", new
        {
            title,
            description = "The original description.",
            repositoryId = host.RepositoryId.ToString(),
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return Str(await ReadJsonAsync(resp), "id")!;
    }

    private static async Task<string> CreateAsAgentAsync(Host host, HttpClient agent, string title)
    {
        var resp = await agent.PostAsJsonAsync("/api/v1/agent/workitems", new
        {
            title,
            description = "",
            repositoryId = host.RepositoryId.ToString(),
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return Str(await ReadJsonAsync(resp), "id")!;
    }

    private static async Task<JsonElement[]> HistoryAsync(Host host, string id)
    {
        var resp = await host.Human.GetAsync($"/api/v1/workitems/{id}/history", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await ReadJsonAsync(resp)).EnumerateArray().ToArray();
    }

    private static void AssertActor(JsonElement entry, string username, string kind, string? detail, string? actorWorkItemId)
    {
        Assert.Equal(username, Str(entry, "username"));
        Assert.Equal(kind, Str(entry, "actorKind"));
        Assert.Equal(detail, Str(entry, "actorDetail"));
        Assert.Equal(actorWorkItemId, Str(entry, "actorWorkItemId"));
    }

    // ── Attribution ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_human_in_the_UI_is_recorded_by_their_username_and_a_manual_park_carries_its_reason()
    {
        var host = _host;
        var id = await CreateAsHumanAsync(host, "Original title");

        var rename = await host.Human.PutAsJsonAsync($"/api/v1/workitems/{id}", new
        {
            title = "Renamed",
            description = "The original description.",
            repositoryId = host.RepositoryId.ToString(),
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var park = await host.Human.PostAsJsonAsync($"/api/v1/workitems/{id}/transition", new { targetStatus = "HumanFeedback" }, Ct);
        Assert.Equal(HttpStatusCode.OK, park.StatusCode);

        var history = await HistoryAsync(host, id);

        var created = history.Where(e => Str(e, "kind") == "Created").ToList();
        Assert.Equal(10, created.Count);
        Assert.All(created, e => AssertActor(e, host.Factory.AdminUsername, "Human", null, null));
        var title = Assert.Single(history, e => Str(e, "kind") == "Changed" && Str(e, "field") == "title");
        Assert.Equal(("Original title", "Renamed"), (Str(title, "oldValue"), Str(title, "newValue")));
        AssertActor(title, host.Factory.AdminUsername, "Human", null, null);
        var status = Assert.Single(history, e => Str(e, "kind") == "Changed" && Str(e, "field") == "status");
        Assert.Equal("HumanFeedback", Str(status, "newValue"));
        Assert.False(string.IsNullOrWhiteSpace(Str(status, "reason")));
        AssertActor(status, host.Factory.AdminUsername, "Human", null, null);
    }

    [Fact]
    public async Task A_loop_runs_changes_are_recorded_as_the_instance_user_naming_the_step_and_the_item_the_run_works_on()
    {
        var host = _host;
        var parent = await CreateAsHumanAsync(host, "The item the run works on");
        var runId = await SeedRunAsync(host.Factory, parent, runningStepLabel: "AI Coder");
        var agent = host.Agent(runId: runId);

        var id = await CreateAsAgentAsync(host, agent, "Follow-up found by the coder");
        var put = await agent.PutAsJsonAsync($"/api/v1/agent/workitems/{id}", new { title = "Follow-up, renamed", description = "" }, Ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var history = await HistoryAsync(host, id);

        var detail = $"AI Coder (run {runId})";
        Assert.All(history.Where(e => Str(e, "kind") == "Created"), e => AssertActor(e, host.Factory.AdminUsername, "Agent", detail, parent));
        var title = Assert.Single(history, e => Str(e, "kind") == "Changed" && Str(e, "field") == "title");
        AssertActor(title, host.Factory.AdminUsername, "Agent", detail, parent);
    }

    [Fact]
    public async Task A_chats_changes_are_recorded_as_the_chats_user_naming_the_chat()
    {
        var host = _host;
        var chatId = await SeedChatSessionAsync(host.Factory, "carol");

        var id = await CreateAsAgentAsync(host, host.Agent(chatSessionId: chatId), "Raised in a chat");

        var created = (await HistoryAsync(host, id)).Where(e => Str(e, "kind") == "Created").ToList();
        Assert.Equal(10, created.Count);
        Assert.All(created, e => AssertActor(e, "carol", "Chat", $"chat {chatId}", null));
    }

    public enum Proposer { LoopRun, Chat }

    [Theory]
    [InlineData(Proposer.LoopRun)]
    [InlineData(Proposer.Chat)]
    public async Task An_approved_proposal_is_recorded_as_the_approver_naming_the_proposal_and_its_proposer(Proposer proposer)
    {
        var host = _host;
        var id = await CreateAsHumanAsync(host, "Old title");
        var parent = await CreateAsHumanAsync(host, "The proposing run's item");
        Guid? runId = null, chatId = null;
        if (proposer == Proposer.LoopRun) runId = await SeedRunAsync(host.Factory, parent, runningStepLabel: "AI Planner");
        else chatId = await SeedChatSessionAsync(host.Factory, "carol");

        var propose = await host.Agent(runId, chatId).PostAsJsonAsync($"/api/v1/agent/workitems/{id}/edit-proposals",
            new { title = "Clearer title", rationale = "The old one is vague." }, Ct);
        Assert.Equal(HttpStatusCode.Created, propose.StatusCode);
        var proposalId = Str(await ReadJsonAsync(propose), "id")!;
        var approve = await host.Human.PostAsync($"/api/v1/workitems/{id}/edit-proposals/{proposalId}/approve", null, Ct);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var title = Assert.Single(await HistoryAsync(host, id), e => Str(e, "kind") == "Changed" && Str(e, "field") == "title");

        Assert.Equal(("Old title", "Clearer title"), (Str(title, "oldValue"), Str(title, "newValue")));
        Assert.Equal(host.Factory.AdminUsername, Str(title, "username"));
        Assert.Equal("Human", Str(title, "actorKind"));
        var detail = Str(title, "actorDetail")!;
        Assert.Contains(proposalId, detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(proposer == Proposer.LoopRun ? $"run {runId}" : $"chat {chatId}", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(proposer == Proposer.LoopRun ? parent : null, Str(title, "actorWorkItemId"));
    }

    // ── Reading it ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_baseline_made_by_a_run_of_this_instance_is_served_with_the_runs_work_item()
    {
        using var server = new FakeWorkItemServerHarness();
        // Own host: it swaps the WorkItem server client for the whole host.
        await using var host = await new Host(services =>
        {
            services.RemoveAll<IWorkItemServerClient>();
            services.AddSingleton(server.Client);
        }).StartAsync();
        var parent = await CreateAsHumanAsync(host, "The item the run works on");
        var localRun = await SeedRunAsync(host.Factory, parent, runningStepLabel: null);
        var goneRun = Guid.NewGuid();
        var byLocalRun = await CreateAsHumanAsync(host, "Made by a run of this instance");
        var byGoneRun = await CreateAsHumanAsync(host, "Made by a run nobody here knows");
        await AddBaselineAsync(server, byLocalRun, localRun);
        await AddBaselineAsync(server, byGoneRun, goneRun);

        var local = (await HistoryAsync(host, byLocalRun)).Where(e => Str(e, "kind") == "Baseline").ToList();
        var gone = (await HistoryAsync(host, byGoneRun)).Where(e => Str(e, "kind") == "Baseline").ToList();

        Assert.Equal(2, local.Count);
        Assert.All(local, e => Assert.Equal(parent, Str(e, "actorWorkItemId")));
        Assert.All(local, e => Assert.Equal($"run {localRun}", Str(e, "actorDetail")));
        Assert.Equal(2, gone.Count);
        Assert.All(gone, e => Assert.Null(Str(e, "actorWorkItemId")));
    }

    /// <summary>What the migration seeds for an item a run created: two of its rows are enough.</summary>
    private static async Task AddBaselineAsync(FakeWorkItemServerHarness server, string workItemId, Guid createdByRun)
    {
        var item = server.ServerDb.WorkItems.Single(w => w.Id == workItemId);
        server.ServerDb.HistoryActor = new ServerActor("fixture", WorkItemActorKind.Human, null, null);
        item.CreatedByLoopRunId = createdByRun;
        await server.ServerDb.SaveChangesAsync(Ct);
        foreach (var (field, value) in new[] { ("title", item.Title), ("status", "Backlog") })
        {
            server.ServerDb.WorkItemHistoryEntries.Add(new WorkItemHistoryEntry
            {
                WorkItemId = workItemId,
                Timestamp = item.CreatedAt.AddSeconds(-1),
                SaveId = $"baseline-{workItemId}",
                Kind = WorkItemHistoryKind.Baseline,
                Username = "instance-user",
                ActorKind = WorkItemActorKind.Agent,
                ActorDetail = $"run {createdByRun}",
                Field = field,
                Value = value,
            });
        }
        await server.ServerDb.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task History_of_an_unknown_item_is_404()
    {
        var host = _host;

        Assert.Equal(HttpStatusCode.NotFound, (await host.Human.GetAsync("/api/v1/workitems/999999/history", Ct)).StatusCode);
    }

    [Fact]
    public async Task History_while_the_server_is_unreachable_is_reported_as_the_outage()
    {
        var outage = new HttpRequestException("Connection refused (workitem-server:8081)");
        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.GetAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(outage);
        client.Setup(c => c.GetHistoryAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(outage);
        // Own host: it swaps the WorkItem server client for the whole host.
        await using var factory = new ApiFactory(configureServices: services => services.ReplaceSingleton(client.Object));
        var human = await factory.CreateAuthenticatedClientAsync();

        var resp = await human.GetAsync("/api/v1/workitems/42/history", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal("WorkItemServer unreachable", Str(await ReadJsonAsync(resp), "error"));
    }

    [Fact]
    public async Task Get_workitem_carries_history_only_when_asked_for()
    {
        var host = _host;
        var id = await CreateAsHumanAsync(host, "Original title");
        await host.Human.PutAsJsonAsync($"/api/v1/workitems/{id}", new
        {
            title = "Renamed",
            description = "The original description.",
            repositoryId = host.RepositoryId.ToString(),
        }, Ct);
        var agent = host.Agent();

        var bare = await agent.GetFromJsonAsync<JsonElement>($"/api/v1/agent/workitems/{id}", Ct);
        var off = await agent.GetFromJsonAsync<JsonElement>($"/api/v1/agent/workitems/{id}?includeHistory=false", Ct);
        var on = await agent.GetFromJsonAsync<JsonElement>($"/api/v1/agent/workitems/{id}?includeHistory=true", Ct);

        Assert.False(bare.TryGetProperty("history", out _));
        Assert.False(off.TryGetProperty("history", out _));
        Assert.Equal("Renamed", Str(on, "title"));
        var history = on.GetProperty("history").EnumerateArray().ToArray();
        Assert.Equal(10, history.Count(e => Str(e, "kind") == "Created"));
        var title = history[0];
        Assert.Equal("title", Str(title, "field"));
        Assert.Equal(("Original title", "Renamed"), (Str(title, "oldValue"), Str(title, "newValue")));
        Assert.Equal("Changed", Str(title, "kind"));
        AssertActor(title, host.Factory.AdminUsername, "Human", null, null);
        Assert.False(string.IsNullOrEmpty(Str(title, "saveId")));
        Assert.True(title.GetProperty("timestamp").TryGetDateTime(out _));
    }

    [Fact]
    public async Task A_deleted_items_history_stays_readable_and_names_who_deleted_it()
    {
        var host = _host;
        var id = await CreateAsHumanAsync(host, "Soon gone");

        Assert.Equal(HttpStatusCode.OK, (await host.Human.DeleteAsync($"/api/v1/workitems/{id}", Ct)).StatusCode);

        var history = await HistoryAsync(host, id);
        var deleted = history[0];
        Assert.Equal("Deleted", Str(deleted, "kind"));
        Assert.Equal("Soon gone", Str(deleted, "newValue"));
        AssertActor(deleted, host.Factory.AdminUsername, "Human", null, null);
        Assert.Equal(10, history.Count(e => Str(e, "kind") == "Created"));

        var agent = host.Agent();
        var asked = await agent.GetAsync($"/api/v1/agent/workitems/{id}?includeHistory=true", Ct);
        Assert.Equal(HttpStatusCode.NotFound, asked.StatusCode);
        Assert.Equal("Deleted", Str((await ReadJsonAsync(asked)).GetProperty("history")[0], "kind"));
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync($"/api/v1/agent/workitems/{id}", Ct)).StatusCode);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    [InlineData(null, "false")]
    public async Task The_get_workitem_tool_passes_includeHistory_through(bool? includeHistory, string expected)
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler(req =>
        {
            seen = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        });
        const string baseAddress = "http://ild-host:8080/";
        var ild = new IldClient(
            new HttpClient(handler) { BaseAddress = new Uri(baseAddress) },
            new IldClientOptions(baseAddress.TrimEnd('/'), "the-agent-token", LoopRunId: null));
        var method = typeof(ILD.McpServer.Tools.ListingTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.CustomAttributes.Any(a =>
                a.AttributeType.Name == "McpServerToolAttribute"
                && a.NamedArguments.Any(n => n.MemberName == "Name" && (string?)n.TypedValue.Value == "get_workitem")));
        var parameter = Assert.Single(method.GetParameters(), p => p.Name == "includeHistory");
        Assert.Equal(false, parameter.DefaultValue);
        var args = method.GetParameters()
            .Select(p => p.Name switch
            {
                "id" => "42",
                "includeHistory" when includeHistory is { } given => given,
                _ => p.DefaultValue,
            })
            .ToArray();

        await (Task<string>)method.Invoke(new ILD.McpServer.Tools.ListingTools(ild), args)!;

        Assert.NotNull(seen);
        Assert.Equal("/api/v1/agent/workitems/42", seen!.RequestUri!.AbsolutePath);
        Assert.Equal(expected, HttpUtility.ParseQueryString(seen.RequestUri.Query)["includeHistory"] ?? "false");
    }

    // ── Seeding ────────────────────────────────────────────────────────────

    /// <summary>A running loop run working on <paramref name="workItemId"/>, with its running step labelled as given.</summary>
    private static async Task<Guid> SeedRunAsync(ApiFactory factory, string workItemId, string? runningStepLabel)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"history-{Guid.NewGuid():N}" };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
        db.LoopTemplates.Add(template);
        db.LoopTemplateVersions.Add(version);
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
        if (runningStepLabel is not null)
        {
            var node = new LoopNode
            {
                Id = Guid.NewGuid(),
                LoopTemplateVersionId = version.Id,
                NodeType = NodeType.AI,
                Label = runningStepLabel,
                CreatedAt = DateTime.UtcNow,
            };
            db.LoopNodes.Add(node);
            db.LoopRunNodes.Add(new LoopRunNode
            {
                Id = Guid.NewGuid(),
                LoopRunId = run.Id,
                LoopNodeId = node.Id,
                NodeLabel = runningStepLabel,
                Status = LoopRunNodeStatus.Running,
                StartedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync(Ct);
        return run.Id;
    }

    private static async Task<Guid> SeedChatSessionAsync(ApiFactory factory, string userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = new ChatSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AiProviderId = Guid.NewGuid(),
            ProviderType = "claude-code",
            ToolAllowlistCsv = "ild",
            ScratchPath = "/tmp/ild-test-chat-session",
            CreatedAt = DateTime.UtcNow,
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(Ct);
        return session.Id;
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
        await db.SaveChangesAsync(Ct);
        return repo.Id;
    }
}
