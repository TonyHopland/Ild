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

        var versions = await db.Context.LoopTemplateVersions.Where(v => v.LoopTemplateId == id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(versions);
        Assert.Equal(1, versions[0].VersionNumber);
        Assert.Equal(3, (await db.Context.LoopNodes.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(2, (await db.Context.LoopNodeEdges.CountAsync(TestContext.Current.CancellationToken)));
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
        Assert.Single(versions);
        Assert.Equal(1, versions.Single().VersionNumber);
    }

    [Fact]
    public async Task Custom_edge_name_round_trips_correctly_and_is_not_corrupted_to_OnSuccess()
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
                new LoopNodeEdgeDto { Id = "e2", SourceNodeId = "h", TargetNodeId = "a", EdgeType = "Custom", Name = "Respond" },
                new LoopNodeEdgeDto { Id = "e3", SourceNodeId = "h", TargetNodeId = "c", EdgeType = "OnSuccess" },
                new LoopNodeEdgeDto { Id = "e4", SourceNodeId = "h", TargetNodeId = "c", EdgeType = "OnFailure" },
                new LoopNodeEdgeDto { Id = "e5", SourceNodeId = "a", TargetNodeId = "c", EdgeType = "OnSuccess" }
            });

        var id = await mgr.CreateLoopTemplateAsync("custom-edge-test", "", graph);

        // The custom edge keeps its role and name in the DB, not corrupted to OnSuccess.
        var edges = await db.Context.LoopNodeEdges.ToListAsync(TestContext.Current.CancellationToken);
        var customEdge = edges.FirstOrDefault(e => e.EdgeType == EdgeType.Custom);
        Assert.NotNull(customEdge);
        Assert.Equal("Respond", customEdge!.Name);

        // Verify round-trip through GetVersionGraph returns Custom + name.
        var versions = await mgr.GetVersionsAsync(id);
        var loadedGraph = await mgr.GetVersionGraphAsync(id, versions.Max(v => v.VersionNumber));
        Assert.NotNull(loadedGraph);
        var loadedCustomEdge = loadedGraph!.Edges.FirstOrDefault(e => e.EdgeType == "Custom");
        Assert.NotNull(loadedCustomEdge);
        Assert.Equal("Respond", loadedCustomEdge!.Name);
    }

    // Start → Human → PR → {AI, Cleanup}. The Human node carries a custom edge
    // named like a reserved PR edge, which is not one because its source is no PR node.
    private static LoopTemplateGraph PrGraph(Dictionary<string, bool?>? userVisible = null)
    {
        LoopNodeEdgeDto Edge(string id, string src, string tgt, string type, string? name = null) => new()
        {
            Id = id, SourceNodeId = src, TargetNodeId = tgt, EdgeType = type, Name = name,
            UserVisible = userVisible?.GetValueOrDefault(id),
        };
        return new(Guid.Empty,
            new() {
                new LoopNodeDto { Id = "s", NodeType = "Start", Label = "Start" },
                new LoopNodeDto { Id = "h", NodeType = "Human", Label = "Review", Config = new() { ["prompt"] = "ok?" } },
                new LoopNodeDto { Id = "p", NodeType = "PR", Label = "Open PR" },
                new LoopNodeDto { Id = "a", NodeType = "AI", Label = "Fix" },
                new LoopNodeDto { Id = "c", NodeType = "Cleanup", Label = "Cleanup" },
            },
            new() {
                Edge("start", "s", "h", "OnSuccess"),
                Edge("human-ok", "h", "p", "OnSuccess"),
                Edge("human-merged", "h", "p", "Custom", "on_merged"),
                Edge("pr-merged", "p", "c", "Custom", "on_merged"),
                Edge("pr-ci-failed", "p", "a", "Custom", "on_ci_failed"),
                Edge("pr-respond", "p", "a", "Custom", "Respond"),
                Edge("pr-ok", "p", "c", "OnSuccess"),
                Edge("pr-fail", "p", "c", "OnFailure"),
                Edge("ai-ok", "a", "c", "OnSuccess"),
            });
    }

    // Saved edges get fresh ids, so they are told apart by source label, role and name.
    private static Dictionary<string, bool?> VisibilityByEdge(LoopTemplateGraph graph)
    {
        var labels = graph.Nodes.ToDictionary(n => n.Id, n => n.Label);
        return graph.Edges.ToDictionary(
            e => $"{labels[e.SourceNodeId]}|{e.EdgeType}|{e.Name}",
            e => e.UserVisible);
    }

    private static Dictionary<string, bool> StoredVisibility(TestDb db, Guid templateId, int version)
    {
        using var fresh = db.Fresh();
        var versionId = fresh.LoopTemplateVersions
            .Single(v => v.LoopTemplateId == templateId && v.VersionNumber == version).Id;
        var nodes = fresh.LoopNodes.AsNoTracking().Where(n => n.LoopTemplateVersionId == versionId).ToList();
        var labels = nodes.ToDictionary(n => n.Id, n => n.Label);
        return fresh.LoopNodeEdges.AsNoTracking().ToList()
            .Where(e => labels.ContainsKey(e.SourceNodeId))
            .ToDictionary(e => $"{labels[e.SourceNodeId]}|{e.EdgeType}|{e.Name}", e => e.UserVisible);
    }

    [Fact]
    public async Task An_edge_saved_without_userVisible_gets_the_creation_default_for_its_source_node()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        var id = await mgr.CreateLoopTemplateAsync("defaults", "", PrGraph());

        var read = VisibilityByEdge((await mgr.GetVersionGraphAsync(id, 1))!);
        Assert.Equal(new Dictionary<string, bool?>
        {
            ["Start|OnSuccess|"] = true,
            ["Review|OnSuccess|"] = true,
            ["Review|Custom|on_merged"] = true,
            ["Open PR|Custom|on_merged"] = false,
            ["Open PR|Custom|on_ci_failed"] = false,
            ["Open PR|Custom|Respond"] = true,
            ["Open PR|OnSuccess|"] = true,
            ["Open PR|OnFailure|"] = true,
            ["Fix|OnSuccess|"] = true,
        }, read);
        Assert.False(StoredVisibility(db, id, 1)["Open PR|Custom|on_merged"]);
    }

    [Fact]
    public async Task An_explicit_userVisible_is_stored_as_sent_and_kept_by_new_versions_and_clones()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        // Both directions against the default: a reserved PR edge made visible,
        // an ordinary edge hidden.
        var id = await mgr.CreateLoopTemplateAsync("explicit", "", PrGraph(new()
        {
            ["pr-merged"] = true,
            ["human-ok"] = false,
            ["pr-respond"] = false,
        }));

        var stored = StoredVisibility(db, id, 1);
        Assert.True(stored["Open PR|Custom|on_merged"]);
        Assert.False(stored["Review|OnSuccess|"]);
        Assert.False(stored["Open PR|Custom|Respond"]);
        Assert.False(stored["Open PR|Custom|on_ci_failed"]);
        Assert.True(stored["Start|OnSuccess|"]);

        // Reload and save again, as the editor does: every value survives as stored.
        var v1 = (await mgr.GetVersionGraphAsync(id, 1))!;
        await mgr.UpdateLoopTemplateAsync(id, "explicit", "", v1);
        var v2 = (await mgr.GetVersionGraphAsync(id, 2))!;
        Assert.Equal(VisibilityByEdge(v1), VisibilityByEdge(v2));
        Assert.Equal(stored, StoredVisibility(db, id, 2));

        var cloneId = await mgr.CloneLoopTemplateAsync(id, "explicit-copy");
        var clone = (await mgr.GetVersionGraphAsync(cloneId, 1))!;
        Assert.Equal(VisibilityByEdge(v1), VisibilityByEdge(clone));
    }

    [Fact]
    public async Task An_edge_row_written_without_the_column_reads_as_visible()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);
        var id = await mgr.CreateLoopTemplateAsync("legacy", "", PrGraph());

        // A row as it existed before the column: the database default decides.
        using (var fresh = db.Fresh())
            fresh.Database.ExecuteSqlRaw(
                "INSERT INTO \"LoopNodeEdges\" (\"Id\", \"SourceNodeId\", \"TargetNodeId\", \"EdgeType\", \"Name\", \"CreatedAt\") " +
                "SELECT lower(hex(randomblob(16))), \"SourceNodeId\", \"TargetNodeId\", \"EdgeType\", 'legacy', \"CreatedAt\" " +
                "FROM \"LoopNodeEdges\" WHERE \"Name\" = 'on_merged' LIMIT 1");

        using var check = db.Fresh();
        var legacy = await check.LoopNodeEdges.AsNoTracking()
            .SingleAsync(e => e.Name == "legacy", TestContext.Current.CancellationToken);
        Assert.True(legacy.UserVisible);
    }
}
