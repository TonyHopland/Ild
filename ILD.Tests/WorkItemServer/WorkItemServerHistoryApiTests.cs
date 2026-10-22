using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Remote;
using ILD.WorkItemServer;
using ILD.WorkItemServer.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RemoteActor = ILD.Core.Services.Remote.WorkItemActor;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// The history contract on the wire: every mutating request names its actor in
/// X-ILD-Actor or is refused before it writes anything, GET history answers the
/// item's changes newest first with each old value paired from the previous
/// entry for the field, and ILD's typed client — which takes the actor on every
/// mutation — sends what the server then records.
/// </summary>
public sealed class WorkItemServerHistoryApiTests : IAsyncLifetime
{
    private const string ApiKey = "history-test-key";
    private const string ActorHeader = "X-ILD-Actor";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private WebApplicationFactory<WorkItemServerProgram> _factory = null!;
    private HttpClient _http = null!;
    private WorkItemServerClient _client = null!;
    private readonly WorkItemServerOptions _opts = new() { BaseUrl = "http://localhost", ApiKey = ApiKey };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _connection.Open();
        Environment.SetEnvironmentVariable("WORKITEM_DB_CONNECTION_STRING", null);
        _factory = new WebApplicationFactory<WorkItemServerProgram>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkItemServer:ApiKeys"] = ApiKey,
                ["Serilog:WriteToConsole"] = "false",
            }));
            b.ConfigureServices(services =>
            {
                services.RemoveHostedService<ILD.WorkItemServer.Hosting.StaleWorkItemReclaimer>();
                services.PostConfigure<ApiKeyOptions>(options => options.Keys = ApiKey);
                var existing = services.FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<WorkItemServerDbContext>));
                if (existing != null) services.Remove(existing);
                services.AddDbContext<WorkItemServerDbContext>(o =>
                {
                    o.UseSqlite(_connection);
                    o.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
                });
            });
        });
        _http = _factory.CreateClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        _client = new WorkItemServerClient(_factory.CreateClient());
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _connection.Dispose();
    }

    private static string Encode(object actor) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(actor));

    private static readonly string Alice = Encode(new { username = "alice", kind = "Human" });

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? actorHeader)
    {
        var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (actorHeader is not null) req.Headers.TryAddWithoutValidation(ActorHeader, actorHeader);
        return await _http.SendAsync(req, Ct);
    }

    private async Task<string> CreateItemAsync(string title = "An item", string? actorHeader = null)
    {
        var resp = await SendAsync(HttpMethod.Post, "/workitems", new { title, description = "First draft" }, actorHeader ?? Alice);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement[]> HistoryAsync(string id)
    {
        var resp = await _http.GetAsync($"/workitems/{id}/history", Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().ToArray();
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    private async Task<int> ItemCountAsync()
        => (await _http.GetFromJsonAsync<JsonElement>("/workitems", Ct)).GetArrayLength();

    // ── X-ILD-Actor ────────────────────────────────────────────────────────

    public static TheoryData<string, string?> UnusableActors => new()
    {
        { "no header", null },
        { "not base64", "%%% not base64 %%%" },
        { "not JSON", Convert.ToBase64String(Encoding.UTF8.GetBytes("alice, Human")) },
        { "blank username", Encode(new { username = "  ", kind = "Human" }) },
        { "username over 256", Encode(new { username = new string('u', 257), kind = "Human" }) },
        { "unknown kind", Encode(new { username = "alice", kind = "Robot" }) },
        { "no kind", Encode(new { username = "alice" }) },
        { "agent without detail", Encode(new { username = "instance-user", kind = "Agent", workItemId = "7" }) },
        { "chat without detail", Encode(new { username = "alice", kind = "Chat" }) },
        { "system without detail", Encode(new { username = "system", kind = "System" }) },
        { "detail over 1024", Encode(new { username = "instance-user", kind = "System", detail = new string('d', 1025) }) },
    };

    [Theory]
    [MemberData(nameof(UnusableActors))]
    public async Task A_mutation_without_a_usable_actor_is_a_400_that_writes_nothing(string _, string? header)
    {
        var id = await CreateItemAsync("Untouched");
        var history = (await HistoryAsync(id)).Length;
        var items = await ItemCountAsync();

        var create = await SendAsync(HttpMethod.Post, "/workitems", new { title = "Should not exist" }, header);
        var update = await SendAsync(HttpMethod.Put, $"/workitems/{id}", new { title = "Should not change" }, header);

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Equal(JsonValueKind.String, (await update.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").ValueKind);
        Assert.Equal(items, await ItemCountAsync());
        Assert.Equal("Untouched", (await _http.GetFromJsonAsync<JsonElement>($"/workitems/{id}", Ct)).GetProperty("title").GetString());
        Assert.Equal(history, (await HistoryAsync(id)).Length);
    }

    public static TheoryData<string, string> MutatingRoutes => new()
    {
        { "DELETE", "/workitems/{id}" },
        { "POST", "/workitems/{id}/transition" },
        { "POST", "/workitems/{id}/dependencies" },
        { "DELETE", "/workitems/{id}/dependencies/{dep}" },
        { "POST", "/workitems/{id}/pull-requests" },
        { "POST", "/workitems/{id}/edit-proposals" },
        { "GET", "/workitems/poll?activeIds={id}" },
    };

    [Theory]
    [MemberData(nameof(MutatingRoutes))]
    public async Task Every_mutating_route_and_the_poll_refuse_a_request_with_no_actor(string method, string route)
    {
        var dep = await CreateItemAsync("Dependency");
        var id = await CreateItemAsync("Target");
        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(HttpMethod.Post, $"/workitems/{id}/dependencies", new { dependencyId = dep }, Alice)).StatusCode);
        var history = (await HistoryAsync(id)).Length;
        object? body = route switch
        {
            "/workitems/{id}/transition" => new { targetStatus = (int)ILD.WorkItemServer.Domain.WorkItemStatus.WorkQueue },
            "/workitems/{id}/dependencies" => new { dependencyId = dep },
            "/workitems/{id}/pull-requests" => new { url = "https://forge.example/acme/widgets/pulls/3" },
            "/workitems/{id}/edit-proposals" => new { title = "Proposed", rationale = "clearer" },
            _ => null,
        };

        var resp = await SendAsync(new HttpMethod(method), route.Replace("{id}", id).Replace("{dep}", dep), body, null);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync($"/workitems/{id}", Ct)).StatusCode);
        Assert.Equal(history, (await HistoryAsync(id)).Length);
    }

    [Fact]
    public async Task Reads_and_health_need_no_actor()
    {
        var id = await CreateItemAsync();

        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/workitems", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync($"/workitems/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync($"/workitems/{id}/dependencies", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync($"/workitems/{id}/history", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().GetAsync("/health", Ct)).StatusCode);
    }

    // ── GET /workitems/{id}/history ────────────────────────────────────────

    [Fact]
    public async Task History_is_newest_first_with_each_old_value_paired_from_the_previous_entry_for_the_field()
    {
        var id = await CreateItemAsync("First title");
        var bob = Encode(new { username = "bob", kind = "Human" });
        var agent = Encode(new { username = "instance-user", kind = "Agent", detail = "AI Coder (run 3f2a)", workItemId = "42" });

        await SendAsync(HttpMethod.Put, $"/workitems/{id}", new { title = "Second title", description = "Second draft" }, bob);
        await SendAsync(HttpMethod.Post, $"/workitems/{id}/transition", new { targetStatus = (int)ILD.WorkItemServer.Domain.WorkItemStatus.WorkQueue, reason = "ready to build" }, agent);
        await SendAsync(HttpMethod.Put, $"/workitems/{id}", new { title = "Third title" }, bob);
        await SendAsync(HttpMethod.Post, $"/workitems/{id}/pull-requests", new { url = "https://forge.example/acme/widgets/pulls/3" }, agent);

        var history = await HistoryAsync(id);

        Assert.Equal(10 + 2 + 1 + 1 + 1, history.Length);
        var stamps = history.Select(e => e.GetProperty("timestamp").GetDateTime()).ToList();
        Assert.Equal(stamps.OrderByDescending(t => t), stamps);
        Assert.Equal(
            new[] { "pullRequest.linked", "title", "status" },
            history.Take(3).Select(e => Str(e, "field")));

        var pr = history[0];
        Assert.Equal("https://forge.example/acme/widgets/pulls/3", Str(pr, "newValue"));
        Assert.Null(Str(pr, "oldValue"));
        Assert.Null(Str(pr, "reason"));

        var titles = history.Where(e => Str(e, "field") == "title").ToList();
        Assert.Equal(
            new (string?, string?)[] { ("Second title", "Third title"), ("First title", "Second title"), (null, "First title") },
            titles.Select(e => (Str(e, "oldValue"), Str(e, "newValue"))));
        Assert.Equal(new[] { "Changed", "Changed", "Created" }, titles.Select(e => Str(e, "kind")));

        var description = Assert.Single(history, e => Str(e, "field") == "description" && Str(e, "kind") == "Changed");
        Assert.Equal(("First draft", "Second draft"), (Str(description, "oldValue"), Str(description, "newValue")));
        Assert.Equal(Str(titles[1], "saveId"), Str(description, "saveId"));
        Assert.Equal("bob", Str(description, "username"));
        Assert.Equal("Human", Str(description, "actorKind"));
        Assert.Null(Str(description, "actorDetail"));
        Assert.Null(Str(description, "actorWorkItemId"));

        var status = history[2];
        Assert.Equal(("Backlog", "WorkQueue"), (Str(status, "oldValue"), Str(status, "newValue")));
        Assert.Equal("ready to build", Str(status, "reason"));
        Assert.Equal("instance-user", Str(status, "username"));
        Assert.Equal("Agent", Str(status, "actorKind"));
        Assert.Equal("AI Coder (run 3f2a)", Str(status, "actorDetail"));
        Assert.Equal("42", Str(status, "actorWorkItemId"));

        var created = history.Where(e => Str(e, "kind") == "Created").ToList();
        Assert.Equal(10, created.Count);
        Assert.Single(created.Select(e => Str(e, "saveId")).Distinct());
        Assert.All(created, e => Assert.Null(Str(e, "oldValue")));
        Assert.All(created, e => Assert.Equal("alice", Str(e, "username")));
    }

    [Fact]
    public async Task History_of_an_unknown_item_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/workitems/999999/history", Ct)).StatusCode);
    }

    [Fact]
    public async Task Deleting_an_item_keeps_its_history()
    {
        var id = await CreateItemAsync();
        var before = await HistoryAsync(id);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/workitems/{id}", null, Alice)).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkItemServerDbContext>();
        Assert.Equal(before.Length + 1, await db.WorkItemHistoryEntries.CountAsync(e => e.WorkItemId == id, Ct));

        // Still readable, and the newest entry says who deleted it.
        var after = await HistoryAsync(id);
        Assert.Equal(before.Length + 1, after.Length);
        Assert.Equal("Deleted", Str(after[0], "kind"));
        Assert.Equal("deleted", Str(after[0], "field"));
        Assert.Equal("An item", Str(after[0], "newValue"));
        Assert.Null(Str(after[0], "oldValue"));
        Assert.Equal("alice", Str(after[0], "username"));
        Assert.Equal("Human", Str(after[0], "actorKind"));
    }

    // ── The typed client ───────────────────────────────────────────────────

    [Fact]
    public async Task The_typed_client_sends_the_actor_it_is_given_and_reads_the_history_back()
    {
        var run = Guid.NewGuid();
        var chat = Guid.NewGuid();

        var item = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest { Title = "From the client" },
            actor: RemoteActor.Human("alice"), ct: Ct);
        await _client.UpdateAsync(_opts, item.Id, new RemoteUpdateWorkItemRequest { Title = "Renamed by the coder" },
            actor: RemoteActor.Agent("instance-user", run, "AI Coder", "42"), ct: Ct);
        await _client.UpdateAsync(_opts, item.Id, new RemoteUpdateWorkItemRequest { Title = "Renamed by a nodeless run" },
            actor: RemoteActor.Agent("instance-user", run, null, "42"), ct: Ct);
        await _client.UpdateAsync(_opts, item.Id, new RemoteUpdateWorkItemRequest { Title = "Renamed in chat" },
            actor: RemoteActor.Chat("carol", chat), ct: Ct);
        await _client.TransitionAsync(_opts, item.Id,
            new RemoteTransitionRequest { TargetStatus = RemoteWorkItemStatus.Ready, Reason = "unblocked" },
            actor: RemoteActor.System("instance-user", "scheduler"), ct: Ct);
        await _client.PollAsync(_opts, new[] { item.Id }, actor: RemoteActor.System("instance-user", "scheduler"), ct: Ct);

        var history = await _client.GetHistoryAsync(_opts, item.Id, Ct);

        Assert.NotNull(history);
        var newestFirst = history!.Where(e => e.Kind.ToString() == "Changed").ToList();
        Assert.Equal(
            new (string?, string?, string?, string?, string?)[]
            {
                ("status", "instance-user", "System", "system: scheduler", null),
                ("title", "carol", "Chat", $"chat {chat}", null),
                ("title", "instance-user", "Agent", $"run {run}", "42"),
                ("title", "instance-user", "Agent", $"AI Coder (run {run})", "42"),
            },
            newestFirst.Select(e => (e.Field, e.Username, e.ActorKind?.ToString(), e.ActorDetail, e.ActorWorkItemId)));
        Assert.Equal("unblocked", newestFirst[0].Reason);
        Assert.Equal(("Renamed by a nodeless run", "Renamed in chat"), (newestFirst[1].OldValue, newestFirst[1].NewValue));
        Assert.All(history.Where(e => e.Kind.ToString() == "Created"), e =>
        {
            Assert.Equal("alice", e.Username);
            Assert.Equal("Human", e.ActorKind?.ToString());
        });
        Assert.Null(await _client.GetHistoryAsync(_opts, "999999", Ct));
    }

    [Fact]
    public async Task Every_mutation_the_typed_client_makes_carries_an_actor_the_server_accepts()
    {
        var actor = RemoteActor.Human("alice");
        var dep = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest { Title = "Dependency" }, actor: actor, ct: Ct);
        var item = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest { Title = "Item" }, actor: actor, ct: Ct);

        Assert.True(await _client.AddDependencyAsync(_opts, item.Id, dep.Id, actor: actor, ct: Ct));
        Assert.True(await _client.RemoveDependencyAsync(_opts, item.Id, dep.Id, actor: actor, ct: Ct));
        Assert.True(await _client.RecordPullRequestAsync(_opts, item.Id, "https://forge.example/acme/widgets/pulls/5",
            loopRunId: null, merged: false, createdAt: null, actor: actor, ct: Ct));

        var upload = await _client.UploadAttachmentsAsync(_opts, item.Id,
            new[] { new RemoteAttachmentUpload("notes.txt", "text/plain", Encoding.UTF8.GetBytes("notes")) }, actor: actor, ct: Ct);
        Assert.Equal(AttachmentUploadOutcome.Created, upload.Outcome);
        Assert.True(await _client.DeleteAttachmentAsync(_opts, item.Id, Assert.Single(upload.Created).Id, actor: actor, ct: Ct));

        var approved = await _client.CreateEditProposalAsync(_opts, item.Id, new RemoteCreateEditProposalRequest { Title = "Approved title" }, actor: actor, ct: Ct);
        var rejected = await _client.CreateEditProposalAsync(_opts, dep.Id, new RemoteCreateEditProposalRequest { Title = "Rejected title" }, actor: actor, ct: Ct);
        Assert.Equal(EditProposalCreateOutcome.Created, approved.Outcome);
        Assert.Equal(EditProposalCreateOutcome.Created, rejected.Outcome);
        Assert.Equal(EditProposalDecisionOutcome.Applied,
            (await _client.ApproveEditProposalAsync(_opts, item.Id, approved.Proposal!.Id, actor: actor, ct: Ct)).Outcome);
        Assert.Equal(EditProposalDecisionOutcome.Rejected,
            (await _client.RejectEditProposalAsync(_opts, dep.Id, rejected.Proposal!.Id, "not now", actor: actor, ct: Ct)).Outcome);
        await _client.MarkEditProposalDecisionsDeliveredAsync(_opts, new[] { approved.Proposal.Id, rejected.Proposal.Id }, actor: actor, ct: Ct);

        Assert.True(await _client.DeleteAsync(_opts, dep.Id, actor: actor, ct: Ct));
        Assert.Equal("Approved title", (await _client.GetAsync(_opts, item.Id, Ct))!.Title);
    }
}
