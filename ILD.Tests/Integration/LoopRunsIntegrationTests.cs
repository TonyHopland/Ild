using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

[Collection("AuthEnvironment")]
public class LoopRunsIntegrationTests
{
    [Fact]
    public async Task GetAll_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/looprins");
        // Path is /api/v1/[controller] -> /api/v1/loopruns
        // Use the correct route below
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Unauthorized });

        var actual = await client.GetAsync("/api/v1/loopruns");
        Assert.Equal(HttpStatusCode.Unauthorized, actual.StatusCode);
    }

    [Fact]
    public async Task GetAll_with_token_returns_200_and_empty_array()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/loopruns");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<object[]>();
        Assert.NotNull(items);
        Assert.Empty(items!);
    }

    [Fact]
    public async Task GetById_for_unknown_id_returns_404()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/loopruns/" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Cleanup is the non-destructive counterpart of Delete (ADR-0008): the run
    /// row and its history survive, but the worktree/branch it named do not, so
    /// the branch is free for the next run.
    /// </summary>
    [Fact]
    public async Task Cleanup_frees_the_branch_and_keeps_the_run_row()
    {
        var reclaimer = new StubRunReclaimer(succeeds: true);
        await using var factory = NewFactory(reclaimer);
        var client = await factory.CreateAuthenticatedClientAsync();
        var runId = SeedRun(factory, LoopRunStatus.Completed, branch: "feature/reclaim-me");

        Assert.Contains("already used locally", await BranchWarningAsync(client, "feature/reclaim-me"));

        var response = await client.PostAsync($"/api/v1/loopruns/{runId}/cleanup", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal([runId], reclaimer.Reclaimed);
        var run = ReadRun(factory, runId);
        Assert.NotNull(run);
        Assert.Null(run!.WorktreePath);
        Assert.Null(run.BranchName);
        Assert.Null(await BranchWarningAsync(client, "feature/reclaim-me"));
    }

    [Fact]
    public async Task Cleanup_is_rejected_for_a_run_that_has_not_finished()
    {
        var reclaimer = new StubRunReclaimer(succeeds: true);
        await using var factory = NewFactory(reclaimer);
        var client = await factory.CreateAuthenticatedClientAsync();
        var runId = SeedRun(factory, LoopRunStatus.Running, branch: "feature/live");

        var response = await client.PostAsync($"/api/v1/loopruns/{runId}/cleanup", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(reclaimer.Reclaimed);
        Assert.Equal("feature/live", ReadRun(factory, runId)!.BranchName);
    }

    [Fact]
    public async Task Cleanup_returns_409_and_leaves_the_run_untouched_when_reclaim_fails()
    {
        var reclaimer = new StubRunReclaimer(succeeds: false);
        await using var factory = NewFactory(reclaimer);
        var client = await factory.CreateAuthenticatedClientAsync();
        var runId = SeedRun(factory, LoopRunStatus.Failed, branch: "feature/stuck");

        var response = await client.PostAsync($"/api/v1/loopruns/{runId}/cleanup", null);

        // Clearing the pointers on a reclaim that did not happen would hide a
        // worktree and branch that are still on disk from every later sweep.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var run = ReadRun(factory, runId);
        Assert.Equal("feature/stuck", run!.BranchName);
        Assert.Equal("/tmp/ild-test-worktree", run.WorktreePath);
    }

    [Fact]
    public async Task Cleanup_for_unknown_id_returns_404()
    {
        await using var factory = NewFactory(new StubRunReclaimer(succeeds: true));
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.PostAsync($"/api/v1/loopruns/{Guid.NewGuid()}/cleanup", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The run list answers whether there is local git state to reclaim, not
    /// where it is: a worktree path is an absolute server path and the cleanup
    /// affordance never needs it. The per-run GET still carries the paths.
    /// </summary>
    [Fact]
    public async Task GetAll_reports_reclaimability_without_exposing_server_paths()
    {
        await using var factory = NewFactory(new StubRunReclaimer(succeeds: true));
        var client = await factory.CreateAuthenticatedClientAsync();
        var runId = SeedRun(factory, LoopRunStatus.Completed, branch: "feature/listed");

        var listed = await client.GetStringAsync("/api/v1/loopruns");

        Assert.DoesNotContain("worktreePath", listed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/tmp/ild-test-worktree", listed, StringComparison.Ordinal);
        using var list = JsonDocument.Parse(listed);
        var row = list.RootElement.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == runId);
        Assert.True(row.GetProperty("hasLocalGitState").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/v1/loopruns/{runId}/cleanup", null)).StatusCode);

        using var after = JsonDocument.Parse(await client.GetStringAsync("/api/v1/loopruns"));
        Assert.False(after.RootElement.EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == runId)
            .GetProperty("hasLocalGitState").GetBoolean());
    }

    private static ApiFactory NewFactory(IRunReclaimer reclaimer)
        => new(configureServices: services => services.ReplaceSingleton(reclaimer));

    private static Guid SeedRun(ApiFactory factory, LoopRunStatus status, string branch)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"cleanup-{Guid.NewGuid():N}" };
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
            WorkItemId = "wi-" + Guid.NewGuid().ToString("N")[..8],
            LoopTemplateVersionId = version.Id,
            Status = status,
            StartedAt = DateTime.UtcNow.AddHours(-2),
            WorktreePath = "/tmp/ild-test-worktree",
            BranchName = branch,
        };
        db.LoopRuns.Add(run);
        db.SaveChanges();
        return run.Id;
    }

    private static LoopRun? ReadRun(ApiFactory factory, Guid runId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.LoopRuns.AsNoTracking().FirstOrDefault(r => r.Id == runId);
    }

    private static async Task<string?> BranchWarningAsync(HttpClient client, string name)
    {
        var response = await client.GetAsync($"/api/v1/workitems/branch-name-check?name={Uri.EscapeDataString(name)}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BranchNameCheck>())!.Warning;
    }

    private sealed record BranchNameCheck(string? Error, string? Warning);

    private sealed class StubRunReclaimer(bool succeeds) : IRunReclaimer
    {
        public List<Guid> Reclaimed { get; } = [];

        public Task<bool> ReclaimLocalStateAsync(LoopRun run)
        {
            Reclaimed.Add(run.Id);
            return Task.FromResult(succeeds);
        }
    }
}
