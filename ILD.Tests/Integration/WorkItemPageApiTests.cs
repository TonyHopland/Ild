using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ILD.Core.Services.Remote;
using ILD.WorkItemServer.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using WiStatus = ILD.WorkItemServer.Domain.WorkItemStatus;

namespace ILD.Tests.Integration;

/// <summary>
/// The taskboard's server-side listing: one status column's page with its total
/// under a search, repository and all-tags filter, the per-status counts under
/// the same filter, and every tag in use. Items are seeded straight into the
/// WorkItem server so creation times and ids are exact.
/// </summary>
public class WorkItemPageApiTests
{
    private static readonly string[] StatusNames =
        ["Backlog", "WorkQueue", "Ready", "Running", "HumanFeedback", "WaitingForIld", "Done"];

    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Host : IAsyncDisposable
    {
        public FakeWorkItemServerHarness Server { get; } = new();
        public ApiFactory Factory { get; }
        public HttpClient Human { get; private set; } = null!;

        public Host()
        {
            Factory = new ApiFactory(configureServices: services => services.ReplaceSingleton(Server.Client));
        }

        public async Task<Host> StartAsync()
        {
            Human = await Factory.CreateAuthenticatedClientAsync();
            return this;
        }

        public HttpClient Agent()
        {
            var client = Factory.CreateClient();
            var token = Factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        public async Task<string> SeedAsync(
            string title,
            WiStatus status = WiStatus.Backlog,
            DateTime? createdAt = null,
            string[]? tags = null,
            Guid? repositoryId = null,
            string? description = null,
            string? id = null)
        {
            var dto = await Server.Service.CreateAsync(new CreateWorkItemRequest
            {
                Title = title,
                Description = description,
                Tags = tags ?? Array.Empty<string>(),
                ForceStatus = status,
                RepositoryId = repositoryId,
            }, TestContext.Current.CancellationToken);

            var entity = await Server.ServerDb.WorkItems.FirstAsync(w => w.Id == dto.Id, TestContext.Current.CancellationToken);
            entity.CreatedAt = createdAt ?? Epoch;
            entity.UpdatedAt = createdAt ?? Epoch;
            if (id is not null) entity.Id = id;
            await Server.ServerDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            return entity.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            Server.Dispose();
        }
    }

    private static async Task<Host> StartHostAsync() => await new Host().StartAsync();

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        var resp = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<(string[] Ids, int Total)> GetPageAsync(HttpClient client, string query)
    {
        var page = await GetJsonAsync(client, $"/api/v1/workitems/page?{query}");
        var ids = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray();
        return (ids, page.GetProperty("total").GetInt32());
    }

    /// <summary>Every id of a filter, fetched page by page the way the board's Load more does.</summary>
    private static async Task<List<string>> PageThroughAsync(HttpClient client, string filter, int take)
    {
        var all = new List<string>();
        for (var skip = 0; ; skip += take)
        {
            var (ids, total) = await GetPageAsync(client, $"{filter}&skip={skip}&take={take}");
            all.AddRange(ids);
            if (ids.Length == 0 || skip + take >= total) return all;
        }
    }

    [Fact]
    public async Task Paging_a_status_reaches_every_item_once_newest_first_with_ties_broken_by_id()
    {
        await using var host = await StartHostAsync();
        // More than the 100 the plain list returns, most of them older than a
        // block of newer Done items, and three to a creation time so the
        // tiebreak decides the order inside each group and across page edges.
        var backlog = new List<(string Id, DateTime CreatedAt)>();
        for (var i = 0; i < 150; i++)
        {
            var createdAt = Epoch.AddHours(i / 3);
            backlog.Add((await host.SeedAsync($"backlog {i}", createdAt: createdAt), createdAt));
        }
        for (var i = 0; i < 30; i++)
            await host.SeedAsync($"done {i}", WiStatus.Done, createdAt: Epoch.AddDays(30).AddHours(i));

        var expected = backlog
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Id, StringComparer.Ordinal)
            .Select(b => b.Id)
            .ToList();

        var paged = await PageThroughAsync(host.Human, "status=Backlog", take: 7);

        Assert.Equal(expected, paged);
        var (firstPage, total) = await GetPageAsync(host.Human, "status=Backlog&skip=0&take=7");
        Assert.Equal(150, total);
        Assert.Equal(expected.Take(7), firstPage);
    }

    [Fact]
    public async Task Search_matches_title_description_or_id_each_on_its_own_case_insensitively()
    {
        await using var host = await StartHostAsync();
        var oldest = await host.SeedAsync("Forgotten chore", createdAt: Epoch, id: "wi-old-7f3a");
        for (var i = 0; i < 25; i++)
            await host.SeedAsync($"newer {i}", createdAt: Epoch.AddDays(1 + i));
        var byTitle = await host.SeedAsync("Fix Login page", createdAt: Epoch.AddDays(40));
        var byDescription = await host.SeedAsync("Session work", createdAt: Epoch.AddDays(41), description: "the LOGIN flow drops tokens");
        await host.SeedAsync("Ends in alpha", createdAt: Epoch.AddDays(42), description: "beta starts here");
        await host.SeedAsync("Login in Done", WiStatus.Done, createdAt: Epoch.AddDays(43));

        var (byId, byIdTotal) = await GetPageAsync(host.Human, "status=Backlog&search=OLD-7F3A&skip=0&take=20");
        Assert.Equal([oldest], byId);
        Assert.Equal(1, byIdTotal);

        var (login, loginTotal) = await GetPageAsync(host.Human, "status=Backlog&search=%20%20login%20&skip=0&take=20");
        Assert.Equal([byDescription, byTitle], login);
        Assert.Equal(2, loginTotal);

        var (acrossFields, acrossTotal) = await GetPageAsync(host.Human, "status=Backlog&search=alpha%20beta&skip=0&take=20");
        Assert.Empty(acrossFields);
        Assert.Equal(0, acrossTotal);

        var (_, blankTotal) = await GetPageAsync(host.Human, "status=Backlog&search=%20%20&skip=0&take=20");
        Assert.Equal(29, blankTotal);
    }

    [Fact]
    public async Task Tags_require_every_requested_tag_and_combine_with_repository_and_status()
    {
        await using var host = await StartHostAsync();
        var repoA = Guid.NewGuid();
        var repoB = Guid.NewGuid();
        var both = await host.SeedAsync("both", createdAt: Epoch.AddDays(1), tags: ["Frontend", "urgent"], repositoryId: repoA);
        var frontendOnly = await host.SeedAsync("frontend only", createdAt: Epoch.AddDays(2), tags: ["frontend"], repositoryId: repoA);
        await host.SeedAsync("urgent only", createdAt: Epoch.AddDays(3), tags: ["urgent"], repositoryId: repoA);
        await host.SeedAsync("untagged", createdAt: Epoch.AddDays(4), repositoryId: repoA);
        var otherRepo = await host.SeedAsync("both, other repo", createdAt: Epoch.AddDays(5), tags: ["frontend", "urgent"], repositoryId: repoB);
        await host.SeedAsync("both, done", WiStatus.Done, createdAt: Epoch.AddDays(6), tags: ["frontend", "urgent"], repositoryId: repoA);

        var (allTags, allTotal) = await GetPageAsync(host.Human, "status=Backlog&tags=frontend&tags=URGENT&skip=0&take=20");
        Assert.Equal([otherRepo, both], allTags);
        Assert.Equal(2, allTotal);

        var (blankIgnored, _) = await GetPageAsync(host.Human, $"status=Backlog&repositoryId={repoA}&tags=frontend&tags=%20&skip=0&take=20");
        Assert.Equal([frontendOnly, both], blankIgnored);

        var (narrowed, narrowedTotal) = await GetPageAsync(host.Human, $"status=Backlog&repositoryId={repoA}&tags=frontend&tags=urgent&skip=0&take=20");
        Assert.Equal([both], narrowed);
        Assert.Equal(1, narrowedTotal);
    }

    [Fact]
    public async Task Page_items_have_the_shape_of_the_plain_list_which_stays_a_bare_array()
    {
        await using var host = await StartHostAsync();
        var repo = Guid.NewGuid();
        await host.SeedAsync("one", createdAt: Epoch.AddDays(1), tags: ["a"], repositoryId: repo, description: "first");
        await host.SeedAsync("two", WiStatus.Ready, createdAt: Epoch.AddDays(2));
        await host.SeedAsync("three", createdAt: Epoch.AddDays(3));

        var plain = await GetJsonAsync(host.Human, "/api/v1/workitems?status=Backlog");
        var page = await GetJsonAsync(host.Human, "/api/v1/workitems/page?status=Backlog");

        Assert.Equal(JsonValueKind.Array, plain.ValueKind);
        Assert.Equal(
            plain.EnumerateArray().Select(i => i.GetRawText()),
            page.GetProperty("items").EnumerateArray().Select(i => i.GetRawText()));
        Assert.Equal(2, page.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Paging_parameters_are_clamped_and_unparseable_filters_ignored()
    {
        await using var host = await StartHostAsync();
        for (var i = 0; i < 499; i++)
            await host.SeedAsync($"backlog {i}", createdAt: Epoch.AddMinutes(i));
        await host.SeedAsync("ready", WiStatus.Ready, createdAt: Epoch.AddMinutes(600));
        await host.SeedAsync("done", WiStatus.Done, createdAt: Epoch.AddMinutes(601));

        var (defaultTake, total) = await GetPageAsync(host.Human, "status=Backlog&take=0");
        Assert.Equal(100, defaultTake.Length);
        Assert.Equal(499, total);

        var (capped, _) = await GetPageAsync(host.Human, "take=1000");
        Assert.Equal(500, capped.Length);

        var (negativeSkip, _) = await GetPageAsync(host.Human, "status=Backlog&skip=-5&take=3");
        var (fromStart, _) = await GetPageAsync(host.Human, "status=Backlog&skip=0&take=3");
        Assert.Equal(fromStart, negativeSkip);

        var (_, unfiltered) = await GetPageAsync(host.Human, "status=NotAStatus&repositoryId=not-a-guid&take=1");
        Assert.Equal(501, unfiltered);
    }

    [Fact]
    public async Task Counts_have_every_status_and_match_the_page_totals_under_the_same_filter()
    {
        await using var host = await StartHostAsync();
        var repo = Guid.NewGuid();
        await host.SeedAsync("a", createdAt: Epoch.AddDays(1), tags: ["x"], repositoryId: repo);
        await host.SeedAsync("b", createdAt: Epoch.AddDays(2), tags: ["x", "y"], repositoryId: repo, description: "needle");
        await host.SeedAsync("c", WiStatus.Ready, createdAt: Epoch.AddDays(3), tags: ["x", "y"], repositoryId: repo, description: "needle");
        await host.SeedAsync("d", WiStatus.Done, createdAt: Epoch.AddDays(4), tags: ["x", "y"], repositoryId: repo, description: "NEEDLE");
        await host.SeedAsync("e", WiStatus.Done, createdAt: Epoch.AddDays(5), tags: ["x", "y"], description: "needle");
        await host.SeedAsync("f", WiStatus.Running, createdAt: Epoch.AddDays(6), tags: ["y"], repositoryId: repo, description: "needle");

        var filter = $"repositoryId={repo}&search=needle&tags=x&tags=y";
        var counts = await GetJsonAsync(host.Human, $"/api/v1/workitems/counts?{filter}");

        Assert.Equal(
            StatusNames.OrderBy(n => n, StringComparer.Ordinal),
            counts.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        foreach (var status in StatusNames)
        {
            var (_, total) = await GetPageAsync(host.Human, $"status={status}&{filter}");
            Assert.Equal(total, counts.GetProperty(status).GetInt32());
        }
        Assert.Equal(1, counts.GetProperty("Backlog").GetInt32());
        Assert.Equal(1, counts.GetProperty("Ready").GetInt32());
        Assert.Equal(1, counts.GetProperty("Done").GetInt32());
        Assert.Equal(0, counts.GetProperty("Running").GetInt32());
        Assert.Equal(0, counts.GetProperty("WaitingForIld").GetInt32());
    }

    [Fact]
    public async Task Tags_lists_every_tag_in_use_once_sorted_case_insensitively_however_old_the_item()
    {
        await using var host = await StartHostAsync();
        await host.SeedAsync("oldest", WiStatus.Done, createdAt: Epoch, tags: ["ancient"]);
        for (var i = 0; i < 110; i++)
            await host.SeedAsync($"newer {i}", createdAt: Epoch.AddHours(1 + i), tags: i % 2 == 0 ? ["banana", "Cherry"] : ["apple"]);

        var tags = await GetJsonAsync(host.Human, "/api/v1/workitems/tags");

        Assert.Equal(
            ["ancient", "apple", "banana", "Cherry"],
            tags.EnumerateArray().Select(t => t.GetString()).ToArray());
    }

    [Fact]
    public async Task The_agent_listing_still_matches_any_requested_tag()
    {
        await using var host = await StartHostAsync();
        var both = await host.SeedAsync("both", createdAt: Epoch.AddDays(1), tags: ["frontend", "urgent"]);
        var frontend = await host.SeedAsync("frontend", createdAt: Epoch.AddDays(2), tags: ["frontend"]);
        var urgent = await host.SeedAsync("urgent", createdAt: Epoch.AddDays(3), tags: ["urgent"]);
        await host.SeedAsync("untagged", createdAt: Epoch.AddDays(4));

        var rows = await GetJsonAsync(host.Agent(), "/api/v1/agent/workitems?tags=frontend&tags=urgent");

        Assert.Equal(
            new[] { both, frontend, urgent }.OrderBy(id => id, StringComparer.Ordinal),
            rows.EnumerateArray().Select(r => r.GetProperty("id").GetString()!).OrderBy(id => id, StringComparer.Ordinal));
    }

    public static TheoryData<string> BoardListingPaths =>
        new() { "/api/v1/workitems/page?status=Backlog", "/api/v1/workitems/counts", "/api/v1/workitems/tags" };

    [Theory]
    [MemberData(nameof(BoardListingPaths))]
    public async Task An_unreachable_WorkItem_server_is_a_503(string path)
    {
        var client = new Mock<IWorkItemServerClient>();
        client.Setup(c => c.ListAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<RemoteWorkItemStatus?>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused (workitem-server:8081)"));
        await using var factory = new ApiFactory(configureServices: services => services.ReplaceSingleton(client.Object));
        var human = await factory.CreateAuthenticatedClientAsync();

        var resp = await human.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Theory]
    [MemberData(nameof(BoardListingPaths))]
    public async Task No_configured_WorkItem_server_is_a_503(string path)
    {
        var options = new Mock<IWorkItemServerOptionsResolver>();
        options.Setup(o => o.ResolveForRepositoryAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No WorkItem server is configured."));
        options.Setup(o => o.ResolveForWorkItemAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No WorkItem server is configured."));
        await using var factory = new ApiFactory(configureServices: services => services.ReplaceSingleton(options.Object));
        var human = await factory.CreateAuthenticatedClientAsync();

        var resp = await human.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }
}
