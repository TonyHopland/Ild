using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using static ILD.Tests.Integration.StorageApiTests;

namespace ILD.Tests.Integration;

/// <summary>
/// The WorkItem server going away partway through a confirmed cleanup: what was
/// already cleaned is reported as cleaned, not lost behind a 503, and nothing
/// after the outage is touched.
/// </summary>
[SupportedOSPlatform("linux")]
public class StorageCleanupOutageTests
{
    [Fact]
    public async Task An_outage_after_a_run_was_cleaned_skips_the_rest_and_still_reports_the_cleaned_one()
    {
        var server = new Mock<IWorkItemServerClient>();
        server.SetupSequence(c => c.GetAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoteWorkItem { Id = "wi-a", Title = "Item", Status = RemoteWorkItemStatus.Done })
            .ThrowsAsync(new HttpRequestException("Connection refused (workitem-server:8081)"));
        var reclaimer = new Mock<IRunReclaimer>();
        reclaimer.Setup(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>())).ReturnsAsync(true);
        await using var factory = new ApiFactory(configureServices: services =>
        {
            services.ReplaceSingleton(server.Object);
            services.ReplaceSingleton(reclaimer.Object);
        });
        var client = await factory.CreateAuthenticatedClientAsync();
        using var scratch = new TempDir();
        var first = await SeedRunAsync(factory, "wi-a", LoopRunStatus.Completed, Path.Combine(scratch.Path, "a"), repositoryId: null);
        var second = await SeedRunAsync(factory, "wi-b", LoopRunStatus.Completed, Path.Combine(scratch.Path, "b"), repositoryId: null);

        var response = await client.PostAsJsonAsync("/api/v1/storage/worktree-cleanup",
            new { runIds = new[] { first, second } }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var results = doc.RootElement.GetProperty("results").EnumerateArray().ToDictionary(r => r.GetProperty("runId").GetGuid());
        Assert.Equal("Cleaned", results[first].GetProperty("outcome").GetString());
        Assert.Equal("Skipped", results[second].GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrWhiteSpace(results[second].GetProperty("reason").GetString()));
        reclaimer.Verify(r => r.ReclaimLocalStateAsync(It.Is<LoopRun>(run => run.Id == second)), Times.Never);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotNull((await db.LoopRuns.AsNoTracking().SingleAsync(r => r.Id == second, TestContext.Current.CancellationToken)).WorktreePath);
    }
}
