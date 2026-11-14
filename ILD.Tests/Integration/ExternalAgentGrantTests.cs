using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// An external-agent grant (ADR-0037): a token the operator hands an AI session outside ILD. It is its own
/// session on the agent API, never the operator, and ends by expiry or revocation.
/// </summary>
public class ExternalAgentGrantTests(ExternalAgentGrantTests.Host host) : IClassFixture<ExternalAgentGrantTests.Host>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed class Host : IAsyncLifetime
    {
        public ApiFactory Factory { get; } = new();
        public HttpClient Operator { get; private set; } = null!;
        public Guid RepositoryId { get; private set; }

        public async ValueTask InitializeAsync()
        {
            Operator = await Factory.CreateAuthenticatedClientAsync();
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var provider = new RemoteProvider { Id = Guid.NewGuid(), Name = "grant-provider", Type = "forgejo", Url = "https://example.invalid", CreatedAt = DateTime.UtcNow };
            var repository = new Repository
            {
                Id = Guid.NewGuid(),
                Name = "repo",
                CloneUrl = "https://example.invalid/repo.git",
                RemoteProviderId = provider.Id,
                DefaultIntakeStatus = WorkItemStatus.Backlog,
                CreatedAt = DateTime.UtcNow,
            };
            db.AddRange(provider, repository);
            await db.SaveChangesAsync();
            RepositoryId = repository.Id;
        }

        public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
    }

    private sealed record Issued(Guid Id, string Token);

    private async Task<Issued> CreateGrantAsync(string name = "claude-session", int lifetimeMinutes = 60, string scope = "Full")
    {
        var response = await host.Operator.PostAsJsonAsync("/api/v1/external-grants", new { name, lifetimeMinutes, scope }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await ReadAsync(response);
        return new Issued(json.GetProperty("grant").GetProperty("id").GetGuid(), json.GetProperty("token").GetString()!);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private HttpRequestMessage As(Issued grant, HttpMethod method, string url, object? body = null, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.Token);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(Issued grant, HttpMethod method, string url, object? body = null, params (string Name, string Value)[] headers)
        => await host.Factory.CreateClient().SendAsync(As(grant, method, url, body, headers), Ct);

    private async Task<string> CreateItemAsync(Issued grant, string title = "item")
    {
        var response = await SendAsync(grant, HttpMethod.Post, "/api/v1/agent/workitems",
            new { title, description = "", repositoryId = host.RepositoryId.ToString() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetString()!;
    }

    private async Task<string> CreateOperatorItemAsync()
    {
        var response = await host.Operator.PostAsJsonAsync("/api/v1/workitems",
            new { title = "operator item", description = "original", repositoryId = host.RepositoryId.ToString() }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync(response)).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task The_token_is_shown_once_and_only_its_hash_is_stored()
    {
        var grant = await CreateGrantAsync("hash-check");

        using var scope = host.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ExternalAgentGrants.AsNoTracking().SingleAsync(g => g.Id == grant.Id, Ct);
        Assert.Equal(SessionTokenHasher.Hash(grant.Token), row.TokenHash);
        Assert.NotEqual(grant.Token, row.TokenHash);

        var list = await host.Operator.GetStringAsync("/api/v1/external-grants", Ct);
        var detail = await host.Operator.GetStringAsync($"/api/v1/external-grants/{grant.Id}", Ct);
        Assert.DoesNotContain(grant.Token, list);
        Assert.DoesNotContain(grant.Token, detail);
        Assert.DoesNotContain(row.TokenHash, list);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(7 * 24 * 60 + 1)]
    public async Task A_lifetime_that_is_not_positive_or_longer_than_a_week_is_refused(int minutes)
    {
        var response = await host.Operator.PostAsJsonAsync("/api/v1/external-grants", new { name = "x", lifetimeMinutes = minutes }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_grant_token_works_on_the_agent_api_and_is_recorded_as_used()
    {
        var grant = await CreateGrantAsync();

        var response = await SendAsync(grant, HttpMethod.Get, "/api/v1/agent/repositories");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await ReadAsync(await host.Operator.GetAsync($"/api/v1/external-grants/{grant.Id}", Ct));
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("grant").GetProperty("lastUsedAt").ValueKind);
        var entry = Assert.Single(detail.GetProperty("activity").EnumerateArray());
        Assert.Equal("/api/v1/agent/repositories", entry.GetProperty("path").GetString());
        Assert.Equal(200, entry.GetProperty("statusCode").GetInt32());
        Assert.Equal("active", detail.GetProperty("grant").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("/api/v1/settings")]
    [InlineData("/api/v1/aiproviders")]
    [InlineData("/api/v1/storage/backups")]
    [InlineData("/api/v1/auth/sessions")]
    [InlineData("/api/v1/external-grants")]
    [InlineData("/api/v1/workitems")]
    public async Task A_grant_token_is_refused_by_the_operator_only_endpoints(string path)
    {
        var grant = await CreateGrantAsync();

        var response = await SendAsync(grant, HttpMethod.Get, path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_grant_cannot_create_or_revoke_grants()
    {
        var grant = await CreateGrantAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(grant, HttpMethod.Post, "/api/v1/external-grants", new { name = "mine", lifetimeMinutes = 60 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(grant, HttpMethod.Post, $"/api/v1/external-grants/{grant.Id}/revoke")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(grant, HttpMethod.Post, "/api/v1/external-grants/revoke-all")).StatusCode);
    }

    [Fact]
    public async Task An_expired_grant_stops_authenticating()
    {
        var grant = await CreateGrantAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(grant, HttpMethod.Get, "/api/v1/agent/repositories")).StatusCode);

        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.ExternalAgentGrants.Where(g => g.Id == grant.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)), Ct);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(grant, HttpMethod.Get, "/api/v1/agent/repositories")).StatusCode);
        var listed = await ReadAsync(await host.Operator.GetAsync("/api/v1/external-grants", Ct));
        Assert.Equal("expired", listed.EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == grant.Id).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_revoked_grant_is_refused_on_the_next_request()
    {
        var grant = await CreateGrantAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(grant, HttpMethod.Get, "/api/v1/agent/repositories")).StatusCode);

        var revoke = await host.Operator.PostAsync($"/api/v1/external-grants/{grant.Id}/revoke", null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(grant, HttpMethod.Get, "/api/v1/agent/repositories")).StatusCode);
        var listed = await ReadAsync(await host.Operator.GetAsync("/api/v1/external-grants", Ct));
        Assert.Equal("revoked", listed.EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == grant.Id).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Revoke_all_ends_every_live_grant()
    {
        var first = await CreateGrantAsync("first");
        var second = await CreateGrantAsync("second");

        var response = await host.Operator.PostAsync("/api/v1/external-grants/revoke-all", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadAsync(response)).GetProperty("revoked").GetInt32() >= 2);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(first, HttpMethod.Get, "/api/v1/agent/repositories")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(second, HttpMethod.Get, "/api/v1/agent/repositories")).StatusCode);
    }

    [Fact]
    public async Task A_grant_token_in_the_query_string_is_refused_even_though_it_is_live()
    {
        var grant = await CreateGrantAsync();

        var response = await host.Factory.CreateClient().GetAsync($"/api/v1/agent/repositories?access_token={grant.Token}", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(grant, HttpMethod.Get, "/api/v1/agent/repositories")).StatusCode);
    }

    [Theory]
    [InlineData("X-ILD-Run-Id")]
    [InlineData("X-ILD-Chat-Session-Id")]
    public async Task A_grant_cannot_act_as_a_run_or_a_chat_through_a_header(string header)
    {
        var grant = await CreateGrantAsync();

        var response = await SendAsync(grant, HttpMethod.Get, "/api/v1/agent/repositories", headers: (header, Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_grant_has_no_run_or_chat_so_their_endpoints_answer_a_clear_4xx()
    {
        var grant = await CreateGrantAsync();

        foreach (var (method, url) in new[]
                 {
                     (HttpMethod.Get, "/api/v1/agent/variables"),
                     (HttpMethod.Get, "/api/v1/agent/current-loop"),
                     (HttpMethod.Post, $"/api/v1/agent/chat/repositories/{host.RepositoryId}/clone"),
                 })
        {
            var response = await SendAsync(grant, method, url);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Only an agent launched for", await response.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task A_grant_edits_and_deletes_only_the_items_it_created()
    {
        var grant = await CreateGrantAsync("owner");
        var other = await CreateGrantAsync("other");
        var own = await CreateItemAsync(grant, "mine");
        var operatorItem = await CreateOperatorItemAsync();
        var othersItem = await CreateItemAsync(other, "theirs");

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(grant, HttpMethod.Put, $"/api/v1/agent/workitems/{own}", new { title = "mine, edited", description = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(grant, HttpMethod.Put, $"/api/v1/agent/workitems/{operatorItem}", new { title = "x", description = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(grant, HttpMethod.Put, $"/api/v1/agent/workitems/{othersItem}", new { title = "x", description = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(grant, HttpMethod.Delete, $"/api/v1/agent/workitems/{operatorItem}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(grant, HttpMethod.Delete, $"/api/v1/agent/workitems/{own}")).StatusCode);

        var item = await ReadAsync(await host.Operator.GetAsync($"/api/v1/workitems/{othersItem}", Ct));
        Assert.Equal(other.Id, item.GetProperty("createdByExternalGrantId").GetGuid());
    }

    [Fact]
    public async Task A_grant_cannot_name_a_run_as_the_creator_of_an_item()
    {
        var grant = await CreateGrantAsync();

        var response = await SendAsync(grant, HttpMethod.Post, "/api/v1/agent/workitems",
            new { title = "x", repositoryId = host.RepositoryId.ToString(), createdByLoopRunId = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_history_names_the_grant_as_an_external_agent()
    {
        var grant = await CreateGrantAsync("history-grant");
        var id = await CreateItemAsync(grant);
        await SendAsync(grant, HttpMethod.Put, $"/api/v1/agent/workitems/{id}", new { title = "renamed", description = "" });

        var history = await ReadAsync(await host.Operator.GetAsync($"/api/v1/workitems/{id}/history", Ct));

        var entries = history.EnumerateArray().ToList();
        Assert.NotEmpty(entries);
        Assert.All(entries, e =>
        {
            Assert.Equal("ExternalAgent", e.GetProperty("actorKind").GetString());
            Assert.Contains("history-grant", e.GetProperty("actorDetail").GetString());
        });
    }

    [Fact]
    public async Task A_proposal_of_a_grant_is_listed_on_the_grant_and_stays_decidable_after_it_is_revoked()
    {
        var grant = await CreateGrantAsync("proposer");
        var item = await CreateOperatorItemAsync();

        var proposed = await SendAsync(grant, HttpMethod.Post, $"/api/v1/agent/workitems/{item}/edit-proposals",
            new { description = "better description", rationale = "clearer" });
        Assert.Equal(HttpStatusCode.Created, proposed.StatusCode);
        var proposalId = (await ReadAsync(proposed)).GetProperty("id").GetString()!;

        var detail = await ReadAsync(await host.Operator.GetAsync($"/api/v1/external-grants/{grant.Id}", Ct));
        var pending = Assert.Single(detail.GetProperty("pendingProposals").EnumerateArray());
        Assert.Equal("workItemEdit", pending.GetProperty("kind").GetString());
        Assert.Equal(item, pending.GetProperty("target").GetString());

        await host.Operator.PostAsync($"/api/v1/external-grants/{grant.Id}/revoke", null, Ct);
        var approved = await host.Operator.PostAsync($"/api/v1/workitems/{item}/edit-proposals/{proposalId}/approve", null, Ct);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var after = await ReadAsync(await host.Operator.GetAsync($"/api/v1/external-grants/{grant.Id}", Ct));
        Assert.Empty(after.GetProperty("pendingProposals").EnumerateArray());
        var changed = await ReadAsync(await host.Operator.GetAsync($"/api/v1/workitems/{item}", Ct));
        Assert.Equal("better description", changed.GetProperty("description").GetString());
    }

    [Fact]
    public async Task A_grant_may_propose_a_global_variable_change_and_read_back_only_its_own()
    {
        var grant = await CreateGrantAsync("globals");
        var other = await CreateGrantAsync("other-globals");

        var proposed = await SendAsync(grant, HttpMethod.Post, "/api/v1/agent/global-variables/proposals",
            new { name = "review_rules", value = "be kind", rationale = "tone" });
        Assert.Equal(HttpStatusCode.Created, proposed.StatusCode);

        var own = await ReadAsync(await SendAsync(grant, HttpMethod.Get, "/api/v1/agent/global-variables/proposals"));
        var others = await ReadAsync(await SendAsync(other, HttpMethod.Get, "/api/v1/agent/global-variables/proposals"));
        Assert.Single(own.EnumerateArray());
        Assert.Empty(others.EnumerateArray());
        var detail = await ReadAsync(await host.Operator.GetAsync($"/api/v1/external-grants/{grant.Id}", Ct));
        Assert.Equal("globalVariable", Assert.Single(detail.GetProperty("pendingProposals").EnumerateArray()).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task The_pages_where_proposals_are_decided_name_the_grant_that_made_them()
    {
        var grant = await CreateGrantAsync("outside-ai");
        var proposed = await SendAsync(grant, HttpMethod.Post, "/api/v1/agent/global-variables/proposals",
            new { name = "tone", value = "kind", rationale = "why" });
        Assert.Equal(HttpStatusCode.Created, proposed.StatusCode);
        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.LoopTemplateProposals.Add(new LoopTemplateProposal
            {
                Id = Guid.NewGuid(),
                LoopTemplateId = Guid.NewGuid(),
                BaseVersion = 1,
                Document = "{}",
                Rationale = "why",
                Status = LoopTemplateProposalStatus.Pending,
                CreatedByExternalGrantId = grant.Id,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var globals = await ReadAsync(await host.Operator.GetAsync("/api/v1/global-variables/proposals?status=Pending", Ct));
        var template = await ReadAsync(await host.Operator.GetAsync("/api/v1/loop-template-proposals?status=Pending", Ct));

        foreach (var list in new[] { globals, template })
        {
            var mine = list.EnumerateArray().Single(p => p.GetProperty("createdByExternalGrantId").ValueKind == JsonValueKind.String
                && p.GetProperty("createdByExternalGrantId").GetGuid() == grant.Id);
            var source = mine.GetProperty("source");
            Assert.Equal("external", source.GetProperty("kind").GetString());
            Assert.Equal(grant.Id, source.GetProperty("grantId").GetGuid());
            Assert.Equal("outside-ai", source.GetProperty("grantName").GetString());
            Assert.False(source.GetProperty("linkable").GetBoolean());
        }
    }

    [Fact]
    public async Task A_read_only_grant_reads_but_neither_creates_nor_proposes()
    {
        var grant = await CreateGrantAsync("reader", scope: "ReadOnly");
        var item = await CreateOperatorItemAsync();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(grant, HttpMethod.Get, $"/api/v1/agent/workitems/{item}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(grant, HttpMethod.Post, "/api/v1/agent/workitems",
            new { title = "x", repositoryId = host.RepositoryId.ToString() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(grant, HttpMethod.Post, $"/api/v1/agent/workitems/{item}/edit-proposals",
            new { description = "y", rationale = "z" })).StatusCode);
    }

    [Fact]
    public async Task A_grant_reaches_the_worktree_routes_of_its_own_items_only()
    {
        var grant = await CreateGrantAsync();
        var own = await CreateItemAsync(grant);
        var operatorItem = await CreateOperatorItemAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(grant, HttpMethod.Post, $"/api/v1/agent/workitems/{operatorItem}/pull-branch")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await SendAsync(grant, HttpMethod.Post, $"/api/v1/agent/workitems/{own}/pull-branch")).StatusCode);
    }

    [Fact]
    public async Task Every_request_a_grant_makes_is_audited_with_its_status()
    {
        var grant = await CreateGrantAsync();
        await SendAsync(grant, HttpMethod.Get, "/api/v1/agent/repositories");
        await SendAsync(grant, HttpMethod.Get, "/api/v1/settings");

        var detail = await ReadAsync(await host.Operator.GetAsync($"/api/v1/external-grants/{grant.Id}", Ct));

        var activity = detail.GetProperty("activity").EnumerateArray().Select(a => (a.GetProperty("path").GetString(), a.GetProperty("statusCode").GetInt32())).ToList();
        Assert.Contains(("/api/v1/agent/repositories", 200), activity);
        Assert.Contains(("/api/v1/settings", 403), activity);
    }

    [Fact]
    public async Task The_discovery_document_is_open_and_reveals_no_secret()
    {
        var response = await host.Factory.CreateClient().GetAsync("/api/v1/agent/_about", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("/mcp", body);
        Assert.Contains("Authorization", body);
        Assert.DoesNotContain(host.Factory.AdminPassword, body);
    }

    [Fact]
    public async Task A_grant_that_asks_too_often_is_limited_and_the_operator_is_not()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?>
        {
            [ILD.Api.Authentication.ExternalAgentRateLimit.EnvVar] = "3",
        });
        var operatorClient = await factory.CreateAuthenticatedClientAsync();
        var response = await operatorClient.PostAsJsonAsync("/api/v1/external-grants", new { name = "busy", lifetimeMinutes = 60 }, Ct);
        var token = (await ReadAsync(response)).GetProperty("token").GetString()!;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await client.GetAsync("/api/v1/agent/repositories", Ct)).StatusCode);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await operatorClient.GetAsync("/api/v1/agent/repositories", Ct)).StatusCode);
    }
}
