using ILD.Data.DTOs;
using ILD.Data.Enums;
using ILD.Core.Services.Implementations;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

public class LoopTemplateManagerTests
{
    private static LoopTemplateGraph MinimalGraph() => new(
        Guid.Empty,
        new() {
            new LoopNodeDto { Id = "s", NodeType = "Start", Label = "Start" },
            new LoopNodeDto { Id = "a", NodeType = "Cmd", Label = "build", Config = new() { ["command"] = "echo hi" } },
            new LoopNodeDto { Id = "c", NodeType = "Cleanup", Label = "Cleanup" }
        },
        new() {
            new LoopNodeEdgeDto { Id = "e1", SourceNodeId = "s", TargetNodeId = "a", EdgeType = "OnSuccess" },
            new LoopNodeEdgeDto { Id = "e2", SourceNodeId = "a", TargetNodeId = "c", EdgeType = "OnSuccess" }
        });

    [Fact]
    public async Task Create_creates_template_with_version_number_1()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        var id = await mgr.CreateLoopTemplateAsync("seed", "desc", MinimalGraph());

        var versions = await db.Context.LoopTemplateVersions.Where(v => v.LoopTemplateId == id).ToListAsync();
        Assert.Equal(1, versions.Count());
        Assert.Equal(1, versions[0].VersionNumber);
        Assert.Equal(3, (await db.Context.LoopNodes.CountAsync()));
        Assert.Equal(2, (await db.Context.LoopNodeEdges.CountAsync()));
    }

    [Fact]
    public async Task Create_invalid_graph_throws()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        var bad = new LoopTemplateGraph(Guid.Empty, new() { new LoopNodeDto { Id = "x", NodeType = "Cmd", Label = "x" } }, new());

        var act = async () => await mgr.CreateLoopTemplateAsync("bad", "", bad);
        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }

    [Fact]
    public async Task Update_creates_a_new_version_each_time()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        var id = await mgr.CreateLoopTemplateAsync("t", "", MinimalGraph());
        await mgr.UpdateLoopTemplateAsync(id, "t", "v2", MinimalGraph());
        await mgr.UpdateLoopTemplateAsync(id, "t", "v3", MinimalGraph());

        var versions = await mgr.GetVersionsAsync(id);
        Assert.Equal(new[] { 1, 2, 3 }, versions.Select(v => v.VersionNumber));
    }

    [Fact]
    public async Task Clone_creates_a_separate_template_with_version_1()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        var srcId = await mgr.CreateLoopTemplateAsync("src", "", MinimalGraph());
        await mgr.UpdateLoopTemplateAsync(srcId, "src", "", MinimalGraph());

        var cloneId = await mgr.CloneLoopTemplateAsync(srcId, "src-copy");

        Assert.NotEqual(srcId, cloneId);
        var clone = await mgr.GetLoopTemplateAsync(cloneId);
        Assert.Equal("src-copy", clone!.Name);
        var versions = await mgr.GetVersionsAsync(cloneId);
        Assert.Equal(1, versions.Count());
        Assert.Equal(1, versions.Single().VersionNumber);
    }

    [Fact]
    public async Task OnRespond_edge_round_trips_correctly_and_is_not_corrupted_to_OnSuccess()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        var graph = new LoopTemplateGraph(Guid.Empty,
            new() {
                new LoopNodeDto { Id = "s", NodeType = "Start", Label = "Start" },
                new LoopNodeDto { Id = "h", NodeType = "Human", Label = "Review", Config = new() { ["prompt"] = "ok?" } },
                new LoopNodeDto { Id = "a", NodeType = "AI", Label = "Iterate" },
                new LoopNodeDto { Id = "c", NodeType = "Cleanup", Label = "Cleanup" }
            },
            new() {
                new LoopNodeEdgeDto { Id = "e1", SourceNodeId = "s", TargetNodeId = "h", EdgeType = "OnSuccess" },
                new LoopNodeEdgeDto { Id = "e2", SourceNodeId = "h", TargetNodeId = "a", EdgeType = "OnRespond" },
                new LoopNodeEdgeDto { Id = "e3", SourceNodeId = "h", TargetNodeId = "c", EdgeType = "OnSuccess" },
                new LoopNodeEdgeDto { Id = "e4", SourceNodeId = "h", TargetNodeId = "c", EdgeType = "OnFailure" },
                new LoopNodeEdgeDto { Id = "e5", SourceNodeId = "a", TargetNodeId = "c", EdgeType = "OnSuccess" }
            });

        var id = await mgr.CreateLoopTemplateAsync("on-respond-test", "", graph);

        // Check that the OnRespond edge was stored as OnRespond in the DB, not corrupted to OnSuccess
        var edges = await db.Context.LoopNodeEdges.ToListAsync();
        var respondEdge = edges.FirstOrDefault(e => e.EdgeType == EdgeType.OnRespond);
        Assert.NotNull(respondEdge);

        // Verify round-trip through GetVersionGraph returns OnRespond
        var versions = await mgr.GetVersionsAsync(id);
        var loadedGraph = await mgr.GetVersionGraphAsync(id, versions.Max(v => v.VersionNumber));
        Assert.NotNull(loadedGraph);
        var loadedRespondEdge = loadedGraph!.Edges.FirstOrDefault(e => e.EdgeType == "OnRespond");
        Assert.NotNull(loadedRespondEdge);
    }
}
