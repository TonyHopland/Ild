using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
