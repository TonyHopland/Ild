using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace ILD.Tests.Integration;

/// <summary>
/// Package feeds through the real API: the feed list a signed-in user manages
/// (and the agent token may not touch), a PAT that goes in and never comes back
/// out, and the repositories that select feeds by name.
/// </summary>
public class PackageFeedsApiTests
{
    private const string Pat = "patSECRET-4f1d-XyZ9";
    private const string CompanyUrl = "https://pkgs.dev.azure.com/example-org/_packaging/company";
    private const string ToolsUrl = "https://pkgs.dev.azure.com/example-org/example-project/_packaging/tools";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClient AgentClient(ApiFactory factory)
    {
        var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<JsonElement> CreateFeedAsync(HttpClient client, string name, string feedUrl, string pat = Pat)
    {
        var response = await client.PostAsJsonAsync("/api/v1/package-feeds", new { name, feedUrl, pat }, Ct);
        Assert.True(response.IsSuccessStatusCode, $"create {name}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return await Json(response);
    }

    private static async Task<JsonElement[]> ListFeedsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/package-feeds", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Json(response)).EnumerateArray().ToArray();
    }

    private static async Task<string?> StoredPatAsync(ApiFactory factory, string id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var feed = await db.Set<PackageFeed>().AsNoTracking().SingleAsync(f => f.Id == Guid.Parse(id), Ct);
        return feed.Pat;
    }

    private static async Task AssertBadRequestWithReasonAsync(HttpResponseMessage response, string because)
    {
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
            $"{because}: expected 400, got {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        var body = await Json(response);
        Assert.True(body.TryGetProperty("error", out var error) && !string.IsNullOrWhiteSpace(error.GetString()),
            $"{because}: the 400 carries no reason");
    }

    private static void AssertNoPat(string body, params string[] pats)
    {
        foreach (var pat in pats)
        {
            Assert.DoesNotContain(pat, body);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(pat)), body);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("ild:" + pat)), body);
        }
    }

    [Fact]
    public async Task The_agent_token_is_refused_at_every_feed_endpoint()
    {
        await using var factory = new ApiFactory();
        var user = await factory.CreateAuthenticatedClientAsync();
        var id = (await CreateFeedAsync(user, "company", CompanyUrl)).GetProperty("id").GetString()!;
        var agent = AgentClient(factory);

        var responses = new[]
        {
            await agent.GetAsync("/api/v1/package-feeds", Ct),
            await agent.PostAsJsonAsync("/api/v1/package-feeds", new { name = "agent-made", feedUrl = ToolsUrl, pat = "p-agent-1234" }, Ct),
            await agent.PutAsJsonAsync($"/api/v1/package-feeds/{id}", new { feedUrl = ToolsUrl, pat = "p-agent-1234" }, Ct),
            await agent.PostAsync($"/api/v1/package-feeds/{id}/test", null, Ct),
            await agent.DeleteAsync($"/api/v1/package-feeds/{id}", Ct),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            AssertNoPat(await response.Content.ReadAsStringAsync(Ct), Pat);
        }
        var only = Assert.Single(await ListFeedsAsync(user));
        Assert.Equal(CompanyUrl, only.GetProperty("feedUrl").GetString());
        Assert.Equal(Pat, await StoredPatAsync(factory, id));
    }

    [Fact]
    public async Task A_feed_is_created_listed_updated_and_deleted_without_its_pat_ever_coming_back()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var createResponse = await client.PostAsJsonAsync("/api/v1/package-feeds", new { name = "company", feedUrl = CompanyUrl + "/", pat = Pat }, Ct);
        Assert.True(createResponse.IsSuccessStatusCode);
        var createdBody = await createResponse.Content.ReadAsStringAsync(Ct);
        AssertNoPat(createdBody, Pat);
        var created = JsonDocument.Parse(createdBody).RootElement;
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("company", created.GetProperty("name").GetString());
        Assert.Equal(CompanyUrl, created.GetProperty("feedUrl").GetString());
        Assert.Equal("••••XyZ9", created.GetProperty("patHint").GetString());
        Assert.True(created.TryGetProperty("createdAt", out _));
        Assert.True(created.TryGetProperty("updatedAt", out _));
        Assert.False(created.TryGetProperty("pat", out _));

        var listResponse = await client.GetAsync("/api/v1/package-feeds", Ct);
        AssertNoPat(await listResponse.Content.ReadAsStringAsync(Ct), Pat);
        var listed = Assert.Single((await Json(listResponse)).EnumerateArray());
        Assert.Equal(id, listed.GetProperty("id").GetString());
        Assert.Equal("••••XyZ9", listed.GetProperty("patHint").GetString());

        // An edit that leaves the PAT out, or blank, keeps the stored one.
        foreach (var edit in new object[] { new { feedUrl = ToolsUrl }, new { feedUrl = CompanyUrl, pat = "" } })
        {
            var kept = await client.PutAsJsonAsync($"/api/v1/package-feeds/{id}", edit, Ct);
            Assert.True(kept.IsSuccessStatusCode, await kept.Content.ReadAsStringAsync(Ct));
            AssertNoPat(await kept.Content.ReadAsStringAsync(Ct), Pat);
            Assert.Equal("••••XyZ9", (await Json(kept)).GetProperty("patHint").GetString());
            Assert.Equal(Pat, await StoredPatAsync(factory, id));
        }
        Assert.Equal(CompanyUrl, Assert.Single(await ListFeedsAsync(client)).GetProperty("feedUrl").GetString());

        const string replacement = "newPAT-0000-abcd";
        var replaced = await client.PutAsJsonAsync($"/api/v1/package-feeds/{id}", new { feedUrl = CompanyUrl, pat = replacement }, Ct);
        Assert.True(replaced.IsSuccessStatusCode);
        AssertNoPat(await replaced.Content.ReadAsStringAsync(Ct), Pat, replacement);
        Assert.Equal("••••abcd", (await Json(replaced)).GetProperty("patHint").GetString());
        Assert.Equal(replacement, await StoredPatAsync(factory, id));

        var deleted = await client.DeleteAsync($"/api/v1/package-feeds/{id}", Ct);
        Assert.True(deleted.IsSuccessStatusCode);
        Assert.Empty(await ListFeedsAsync(client));
    }

    [Theory]
    [InlineData("abcd1234", "••••1234")]
    [InlineData("abc1234", "••••")]
    [InlineData("x", "••••")]
    public async Task The_pat_hint_shows_the_last_four_characters_only_of_a_pat_long_enough_to_spare_them(string pat, string hint)
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var created = await CreateFeedAsync(client, "company", CompanyUrl, pat);

        Assert.Equal(hint, created.GetProperty("patHint").GetString());
    }

    [Fact]
    public async Task A_bad_name_or_missing_pat_is_refused_with_a_reason_and_nothing_is_stored()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        await CreateFeedAsync(client, "company", CompanyUrl);

        var cases = new (object Body, string Because)[]
        {
            (new { name = "", feedUrl = ToolsUrl, pat = Pat }, "empty name"),
            (new { name = "   ", feedUrl = ToolsUrl, pat = Pat }, "blank name"),
            (new { name = new string('n', 129), feedUrl = ToolsUrl, pat = Pat }, "name over 128 characters"),
            (new { name = "COMPANY", feedUrl = ToolsUrl, pat = Pat }, "name taken, ignoring case"),
            (new { name = "tools", feedUrl = ToolsUrl }, "no pat"),
            (new { name = "tools", feedUrl = ToolsUrl, pat = "" }, "empty pat"),
        };
        foreach (var (body, because) in cases)
            await AssertBadRequestWithReasonAsync(await client.PostAsJsonAsync("/api/v1/package-feeds", body, Ct), because);

        Assert.Single(await ListFeedsAsync(client));
        await CreateFeedAsync(client, new string('n', 128), ToolsUrl);
    }

    [Fact]
    public async Task Only_an_azure_artifacts_feed_url_is_accepted()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = (await CreateFeedAsync(client, "company", CompanyUrl)).GetProperty("id").GetString()!;

        var rejected = new[]
        {
            "",
            "not a url",
            "http://pkgs.dev.azure.com/example-org/_packaging/company",
            "https://example.com/example-org/_packaging/company",
            "https://example-org.pkgs.visualstudio.com/_packaging/company",
            "https://pkgs.dev.azure.com/_packaging/company",
            "https://pkgs.dev.azure.com/example-org/_packaging",
            "https://pkgs.dev.azure.com/example-org/_packaging/",
            "https://pkgs.dev.azure.com/example-org/example-project/team/_packaging/company",
            "https://pkgs.dev.azure.com/example-org/_packaging/company/nuget/v3/index.json",
            "https://pkgs.dev.azure.com/example-org/_packaging/company/npm/registry/",
            "https://pkgs.dev.azure.com/example-org/_packaging/company//",
            "https://pkgs.dev.azure.com/example-org/_packaging/company?api-version=7.1",
            "https://pkgs.dev.azure.com/example-org/_packaging/company#top",
        };
        var n = 0;
        foreach (var url in rejected)
        {
            await AssertBadRequestWithReasonAsync(
                await client.PostAsJsonAsync("/api/v1/package-feeds", new { name = $"feed-{n++}", feedUrl = url, pat = Pat }, Ct),
                $"create with '{url}'");
            await AssertBadRequestWithReasonAsync(
                await client.PutAsJsonAsync($"/api/v1/package-feeds/{id}", new { feedUrl = url }, Ct),
                $"update to '{url}'");
        }

        Assert.Equal(CompanyUrl, Assert.Single(await ListFeedsAsync(client)).GetProperty("feedUrl").GetString());
        Assert.Equal(ToolsUrl, (await CreateFeedAsync(client, "tools", ToolsUrl + "/")).GetProperty("feedUrl").GetString());
    }

    [Fact]
    public async Task A_feed_keeps_its_name_through_an_update()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = (await CreateFeedAsync(client, "company", CompanyUrl)).GetProperty("id").GetString()!;

        var response = await client.PutAsJsonAsync($"/api/v1/package-feeds/{id}", new { name = "renamed", feedUrl = CompanyUrl }, Ct);

        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.BadRequest,
            $"unexpected {(int)response.StatusCode}");
        Assert.Equal("company", Assert.Single(await ListFeedsAsync(client)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_test_endpoint_tests_the_stored_feed_and_answers_404_for_an_unknown_one()
    {
        var tester = new Mock<IConnectionTester>();
        tester.Setup(t => t.TestPackageFeedAsync(It.IsAny<PackageFeed>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConnectionTestResult(ConnectionTestOutcome.InvalidApiKey,
                "PAT rejected, probably expired or revoked. Create a new one with Packaging (Read) and paste it here.", "HTTP 401"));
        await using var factory = new ApiFactory(configureServices: services =>
        {
            services.RemoveAll<IConnectionTester>();
            services.AddSingleton(tester.Object);
        });
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = (await CreateFeedAsync(client, "company", CompanyUrl)).GetProperty("id").GetString()!;

        var response = await client.PostAsync($"/api/v1/package-feeds/{id}/test", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.StartsWith("PAT rejected", body.GetProperty("message").GetString());
        Assert.True(body.TryGetProperty("outcome", out _));
        Assert.True(body.TryGetProperty("detail", out _));
        tester.Verify(t => t.TestPackageFeedAsync(
            It.Is<PackageFeed>(f => f.Id == Guid.Parse(id) && f.FeedUrl == CompanyUrl && f.Pat == Pat),
            It.IsAny<CancellationToken>()), Times.Once);

        var unknown = await client.PostAsync($"/api/v1/package-feeds/{Guid.NewGuid()}/test", null, Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // ── Repositories selecting feeds ──────────────────────────────────────

    private static async Task<string> SeedProviderAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provider = new RemoteProvider { Id = Guid.NewGuid(), Name = "prov", Type = "Forgejo", Url = "https://git.example.com" };
        db.RemoteProviders.Add(provider);
        await db.SaveChangesAsync(Ct);
        return provider.Id.ToString();
    }

    private static object RepoPayload(string providerId, string[]? packageFeeds) => new
    {
        name = "my-repo",
        cloneUrl = "https://git.example.com/my-repo.git",
        defaultBranch = "main",
        remoteProviderId = providerId,
        defaultIntakeStatus = "Backlog",
        packageFeeds,
    };

    private static (string Name, bool Missing)[] Selection(JsonElement repo)
        => repo.GetProperty("packageFeeds").EnumerateArray()
            .Select(f => (f.GetProperty("name").GetString()!, f.GetProperty("missing").GetBoolean()))
            .ToArray();

    private static async Task<(string Name, bool Missing)[]> SelectionOfAsync(HttpClient client, string repoId)
    {
        var response = await client.GetAsync($"/api/v1/repositories/{repoId}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Selection(await Json(response));
    }

    [Fact]
    public async Task A_repository_selects_no_feeds_unless_told_and_refuses_one_that_does_not_exist()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var providerId = await SeedProviderAsync(factory);
        await CreateFeedAsync(client, "company", CompanyUrl);

        var plain = await client.PostAsJsonAsync("/api/v1/repositories", RepoPayload(providerId, null), Ct);
        Assert.Equal(HttpStatusCode.Created, plain.StatusCode);
        Assert.Empty(Selection(await Json(plain)));

        await AssertBadRequestWithReasonAsync(
            await client.PostAsJsonAsync("/api/v1/repositories", RepoPayload(providerId, ["company", "nope"]), Ct),
            "create selecting an unknown feed");
        var repos = await (await client.GetAsync("/api/v1/repositories", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Single(repos.EnumerateArray());
    }

    [Fact]
    public async Task A_selected_feed_that_is_deleted_shows_as_missing_can_be_kept_and_resolves_again_when_recreated()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var providerId = await SeedProviderAsync(factory);
        var company = (await CreateFeedAsync(client, "company", CompanyUrl)).GetProperty("id").GetString()!;
        await CreateFeedAsync(client, "tools", ToolsUrl);

        var createResponse = await client.PostAsJsonAsync("/api/v1/repositories", RepoPayload(providerId, ["tools", "company"]), Ct);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await Json(createResponse);
        var repoId = created.GetProperty("id").GetString()!;
        Assert.Equal(new[] { ("company", false), ("tools", false) }, Selection(created));

        var deleted = await client.DeleteAsync($"/api/v1/package-feeds/{company}", Ct);
        Assert.True(deleted.IsSuccessStatusCode);
        Assert.Equal(new[] { ("company", true), ("tools", false) }, await SelectionOfAsync(client, repoId));

        // An edit that says nothing about feeds leaves the selection as it was.
        var untouched = await client.PutAsJsonAsync($"/api/v1/repositories/{repoId}", RepoPayload(providerId, null), Ct);
        Assert.Equal(HttpStatusCode.OK, untouched.StatusCode);
        Assert.Equal(new[] { ("company", true), ("tools", false) }, Selection(await Json(untouched)));

        var kept = await client.PutAsJsonAsync($"/api/v1/repositories/{repoId}", RepoPayload(providerId, ["company", "tools"]), Ct);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(new[] { ("company", true), ("tools", false) }, Selection(await Json(kept)));

        await AssertBadRequestWithReasonAsync(
            await client.PutAsJsonAsync($"/api/v1/repositories/{repoId}", RepoPayload(providerId, ["company", "tools", "nope"]), Ct),
            "update adding an unknown feed");
        Assert.Equal(new[] { ("company", true), ("tools", false) }, await SelectionOfAsync(client, repoId));

        await CreateFeedAsync(client, "company", CompanyUrl);
        Assert.Equal(new[] { ("company", false), ("tools", false) }, await SelectionOfAsync(client, repoId));

        var dropped = await client.PutAsJsonAsync($"/api/v1/repositories/{repoId}", RepoPayload(providerId, []), Ct);
        Assert.Equal(HttpStatusCode.OK, dropped.StatusCode);
        Assert.Empty(Selection(await Json(dropped)));
    }

    [Theory]
    [InlineData("/api/v1/workitems/WI-7/preview/start", false)]
    [InlineData("/api/v1/workitems/WI-7/preview/services/web/start", false)]
    [InlineData("/api/v1/agent/workitems/WI-7/preview/start", true)]
    [InlineData("/api/v1/agent/workitems/WI-7/preview/services/web/start", true)]
    public async Task Starting_a_preview_hands_it_the_feeds_of_the_runs_repository(string route, bool asAgent)
    {
        var repoId = Guid.Empty;
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync("WI-7")).ReturnsAsync(() => new WorkItemView
        {
            Id = "WI-7",
            Title = "T",
            WorktreePath = "/tmp/ild-feed-preview-wt",
            RunRepositoryId = repoId,
        });
        var started = new List<WorktreePreviewStartOptions?>();
        var preview = new Mock<IWorktreePreviewService>();
        var running = new WorktreePreviewResponse { State = "running", WorktreePath = "/tmp/ild-feed-preview-wt" };
        preview.Setup(p => p.StartAsync(It.IsAny<string>(), It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, WorktreePreviewStartOptions?, CancellationToken>((_, o, _) => started.Add(o))
            .ReturnsAsync(running);
        preview.Setup(p => p.StartServiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WorktreePreviewStartOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, WorktreePreviewStartOptions?, CancellationToken>((_, _, o, _) => started.Add(o))
            .ReturnsAsync(running);
        await using var factory = new ApiFactory(configureServices: services =>
        {
            services.RemoveAll<IWorkItemManager>();
            services.AddSingleton(workItems.Object);
            services.RemoveAll<IWorktreePreviewService>();
            services.AddSingleton(preview.Object);
        });
        var user = await factory.CreateAuthenticatedClientAsync();
        var providerId = await SeedProviderAsync(factory);
        await CreateFeedAsync(user, "company", CompanyUrl);
        var created = await user.PostAsJsonAsync("/api/v1/repositories", RepoPayload(providerId, ["company"]), Ct);
        repoId = Guid.Parse((await Json(created)).GetProperty("id").GetString()!);

        var client = asAgent ? AgentClient(factory) : user;
        var response = await client.PostAsJsonAsync(route, new { }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var options = Assert.Single(started);
        var feed = Assert.Single(options!.PackageFeeds!);
        Assert.Equal("company", feed.Name);
        Assert.Equal(Pat, feed.Pat);
    }

    [Fact]
    public async Task No_repository_or_agent_payload_carries_a_selected_feeds_pat()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var providerId = await SeedProviderAsync(factory);
        await CreateFeedAsync(client, "company", CompanyUrl);

        var created = await client.PostAsJsonAsync("/api/v1/repositories", RepoPayload(providerId, ["company"]), Ct);
        AssertNoPat(await created.Content.ReadAsStringAsync(Ct), Pat);
        var repoId = (await Json(created)).GetProperty("id").GetString()!;

        var bodies = new[]
        {
            await (await client.GetAsync("/api/v1/repositories", Ct)).Content.ReadAsStringAsync(Ct),
            await (await client.GetAsync($"/api/v1/repositories/{repoId}", Ct)).Content.ReadAsStringAsync(Ct),
            await (await client.PutAsJsonAsync($"/api/v1/repositories/{repoId}", RepoPayload(providerId, ["company"]), Ct)).Content.ReadAsStringAsync(Ct),
            await (await AgentClient(factory).GetAsync("/api/v1/agent/repositories", Ct)).Content.ReadAsStringAsync(Ct),
        };

        Assert.Contains("my-repo", bodies[^1]);
        foreach (var body in bodies)
            AssertNoPat(body, Pat);
    }
}
