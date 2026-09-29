using System.Text.Json.Nodes;
using ILD.Core.Services.Implementations;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Migrations;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

public class NodeOutputsMigratorTests
{
    private static readonly string[] Reserved =
    {
        "on_rejected", "on_merge_conflict", "on_ci_failed", "on_comment",
        "on_approved", "on_ci_passed", "on_merged", "on_abandoned",
    };

    private sealed record LegacyVersion(Guid VersionId, Dictionary<string, Guid> NodeIds);

    /// <summary>
    /// Seeds one version of a pre-outputs loop: output names live in customEdges,
    /// matchRules and cases/defaultEdge, and one Human output exists only as an
    /// edge row. Every node type that can have outputs is present.
    /// </summary>
    private static LegacyVersion SeedLegacyVersion(TestDb db, Guid templateId, int versionNumber, string reviewConfig)
    {
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = templateId, VersionNumber = versionNumber };
        db.Context.LoopTemplateVersions.Add(version);

        LoopNode Node(NodeType type, string label, string? config) => new()
        {
            Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = type, Label = label, Config = config,
        };

        var start = Node(NodeType.Start, "start", "{\"createWorktree\":true}");
        var review = Node(NodeType.Human, "review", reviewConfig);
        var ai = Node(NodeType.AI, "ai", "{\"prompt\":\"Review it\",\"matchRules\":[{\"pattern\":\"REJECT\",\"edgeName\":\"Reject\"}]}");
        var gate = Node(NodeType.Condition, "gate",
            "{\"cases\":[{\"variant\":\"PrExists\",\"edgeName\":\" has-pr \"}],\"defaultEdge\":\"otherwise \",\"output\":\"{{Node.Input}}\"}");
        var pr = Node(NodeType.PR, "pr", "{\"prDescriptionTemplate\":\"t\",\"customEdges\":[\"deploy\"]}");
        var cleanup = Node(NodeType.Cleanup, "cleanup", null);
        db.Context.LoopNodes.AddRange(start, review, ai, gate, pr, cleanup);

        LoopNodeEdge Edge(LoopNode from, LoopNode to, EdgeType type, string? name = null) => new()
        {
            Id = Guid.NewGuid(), SourceNodeId = from.Id, TargetNodeId = to.Id, EdgeType = type, Name = name,
        };

        db.Context.LoopNodeEdges.AddRange(
            Edge(start, review, EdgeType.OnSuccess),
            Edge(review, ai, EdgeType.OnSuccess),
            Edge(review, ai, EdgeType.Custom, "Respond"),
            Edge(review, cleanup, EdgeType.Custom, "Escalate"),
            Edge(ai, gate, EdgeType.OnSuccess),
            Edge(ai, review, EdgeType.Custom, "Reject"),
            Edge(gate, pr, EdgeType.Custom, "has-pr"),
            Edge(gate, cleanup, EdgeType.Custom, "otherwise"),
            Edge(pr, cleanup, EdgeType.Custom, "on_merged"),
            Edge(pr, cleanup, EdgeType.Custom, "on_abandoned"),
            Edge(pr, cleanup, EdgeType.Custom, "deploy"));

        return new LegacyVersion(version.Id, new()
        {
            ["start"] = start.Id, ["review"] = review.Id, ["ai"] = ai.Id,
            ["gate"] = gate.Id, ["pr"] = pr.Id, ["cleanup"] = cleanup.Id,
        });
    }

    private static Guid SeedTemplate(TestDb db)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "Review Loop", RecoveryPolicy = RecoveryPolicy.AutoResume };
        db.Context.LoopTemplates.Add(template);
        return template.Id;
    }

    private static async Task<JsonObject> ConfigOf(TestDb db, Guid nodeId)
    {
        var node = await db.Fresh().LoopNodes.SingleAsync(n => n.Id == nodeId, TestContext.Current.CancellationToken);
        return Assert.IsType<JsonObject>(JsonNode.Parse(node.Config!));
    }

    private static List<JsonObject> Outputs(JsonObject config)
        => Assert.IsType<JsonArray>(config["outputs"]).Select(o => Assert.IsType<JsonObject>(o)).ToList();

    private static List<string> OutputNames(JsonObject config)
        => Outputs(config).Select(o => (string)o["name"]!).ToList();

    private static async Task<Dictionary<Guid, string?>> ConfigSnapshot(TestDb db)
        => await db.Fresh().LoopNodes.ToDictionaryAsync(n => n.Id, n => n.Config, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Every_version_gets_outputs_from_all_the_old_sources_and_loses_customEdges()
    {
        using var db = new TestDb();
        var templateId = SeedTemplate(db);
        const string legacyReview = "{\"prompt\":\"ok?\",\"customEdges\":[\"Respond\",\"\",\"  \"]}";
        var old = SeedLegacyVersion(db, templateId, 1, legacyReview);
        var latest = SeedLegacyVersion(db, templateId, 2, legacyReview);
        db.Context.SaveChanges();

        var migrated = await NodeOutputsMigrator.MigrateAsync(db.Context, TestContext.Current.CancellationToken);

        Assert.Equal(12, migrated);
        foreach (var version in new[] { old, latest })
        {
            var expected = new Dictionary<string, string[]>
            {
                ["start"] = new[] { "OnSuccess", "OnFailure" },
                ["review"] = new[] { "OnSuccess", "OnFailure", "Respond", "Escalate" },
                ["ai"] = new[] { "OnSuccess", "OnFailure", "Reject" },
                ["gate"] = new[] { "OnFailure", "has-pr", "otherwise" },
                ["pr"] = new[] { "OnSuccess", "OnFailure", "deploy" }.Concat(Reserved).ToArray(),
                ["cleanup"] = Array.Empty<string>(),
            };
            foreach (var (label, names) in expected)
            {
                var config = await ConfigOf(db, version.NodeIds[label]);
                var actual = OutputNames(config);
                Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), actual.OrderBy(n => n, StringComparer.Ordinal));
                Assert.False(config.ContainsKey("customEdges"), $"{label} still carries customEdges");

                foreach (var output in Outputs(config))
                {
                    var isReserved = label == "pr" && Reserved.Contains((string)output["name"]!);
                    if (isReserved)
                        Assert.True((bool)output["reserved"]!, $"{output["name"]} is not marked reserved");
                    else
                        Assert.False(output.ContainsKey("reserved"), $"{label}: {output["name"]} is marked reserved");
                }
            }

            // Everything that was not an output source is left as it was.
            var gate = await ConfigOf(db, version.NodeIds["gate"]);
            Assert.Equal(" has-pr ", (string)gate["cases"]![0]!["edgeName"]!);
            Assert.Equal("otherwise ", (string)gate["defaultEdge"]!);
            Assert.Equal("{{Node.Input}}", (string)gate["output"]!);
            var ai = await ConfigOf(db, version.NodeIds["ai"]);
            Assert.Equal("Reject", (string)ai["matchRules"]![0]!["edgeName"]!);
            Assert.Equal("Review it", (string)ai["prompt"]!);
            Assert.True((bool)(await ConfigOf(db, version.NodeIds["start"]))["createWorktree"]!);
        }
    }

    [Fact]
    public async Task An_output_already_declared_keeps_its_fields_and_is_not_added_twice()
    {
        using var db = new TestDb();
        var templateId = SeedTemplate(db);
        var version = SeedLegacyVersion(db, templateId, 1,
            "{\"prompt\":\"ok?\",\"outputs\":[{\"name\":\"Respond\",\"visible\":false,\"color\":\"x\"}],\"customEdges\":[\"Respond\"]}");
        db.Context.SaveChanges();

        await NodeOutputsMigrator.MigrateAsync(db.Context, TestContext.Current.CancellationToken);

        var outputs = Outputs(await ConfigOf(db, version.NodeIds["review"]));
        Assert.Single(outputs, o => (string)o["name"]! == "Respond");
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("{\"name\":\"Respond\",\"visible\":false,\"color\":\"x\"}"),
            outputs[0]));
    }

    [Fact]
    public async Task A_second_run_rewrites_nothing_and_changes_no_bytes()
    {
        using var db = new TestDb();
        var templateId = SeedTemplate(db);
        SeedLegacyVersion(db, templateId, 1, "{\"prompt\":\"ok?\",\"customEdges\":[\"Respond\"]}");
        SeedLegacyVersion(db, templateId, 2, "{\"prompt\":\"ok?\",\"outputs\":[{\"name\":\"Respond\",\"visible\":false}]}");
        db.Context.SaveChanges();

        Assert.True(await NodeOutputsMigrator.MigrateAsync(db.Context, TestContext.Current.CancellationToken) > 0);
        var afterFirst = await ConfigSnapshot(db);

        Assert.Equal(0, await NodeOutputsMigrator.MigrateAsync(db.Context, TestContext.Current.CancellationToken));
        Assert.Equal(afterFirst, await ConfigSnapshot(db));
    }

    [Fact]
    public async Task A_node_whose_only_difference_is_formatting_is_not_rewritten()
    {
        using var db = new TestDb();
        var templateId = SeedTemplate(db);
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = templateId, VersionNumber = 1 };
        db.Context.LoopTemplateVersions.Add(version);
        var start = new LoopNode
        {
            Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.Start, Label = "start",
            Config = "{ \"createWorktree\": true,\n  \"outputs\": [ { \"name\": \"OnFailure\" }, { \"name\": \"OnSuccess\" } ] }",
        };
        var cleanup = new LoopNode
        {
            Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.Cleanup, Label = "cleanup",
            Config = "{ \"outputs\": [ ] }",
        };
        db.Context.LoopNodes.AddRange(start, cleanup);
        db.Context.LoopNodeEdges.Add(new LoopNodeEdge
        {
            Id = Guid.NewGuid(), SourceNodeId = start.Id, TargetNodeId = cleanup.Id, EdgeType = EdgeType.OnSuccess,
        });
        db.Context.SaveChanges();
        var before = await ConfigSnapshot(db);

        var migrated = await NodeOutputsMigrator.MigrateAsync(db.Context, TestContext.Current.CancellationToken);

        Assert.Equal(0, migrated);
        Assert.Equal(before, await ConfigSnapshot(db));
    }

    [Fact]
    public async Task Edge_rows_and_node_ids_are_left_alone()
    {
        using var db = new TestDb();
        var templateId = SeedTemplate(db);
        SeedLegacyVersion(db, templateId, 1, "{\"prompt\":\"ok?\",\"customEdges\":[\"Respond\"]}");
        db.Context.SaveChanges();
        var edgesBefore = await db.Fresh().LoopNodeEdges
            .Select(e => new { e.Id, e.SourceNodeId, e.TargetNodeId, e.EdgeType, e.Name })
            .OrderBy(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);
        var nodeIdsBefore = await db.Fresh().LoopNodes.Select(n => n.Id).OrderBy(id => id).ToListAsync(TestContext.Current.CancellationToken);

        await NodeOutputsMigrator.MigrateAsync(db.Context, TestContext.Current.CancellationToken);

        var edgesAfter = await db.Fresh().LoopNodeEdges
            .Select(e => new { e.Id, e.SourceNodeId, e.TargetNodeId, e.EdgeType, e.Name })
            .OrderBy(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(edgesBefore, edgesAfter);
        Assert.Equal(nodeIdsBefore,
            await db.Fresh().LoopNodes.Select(n => n.Id).OrderBy(id => id).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Every_migrated_version_passes_the_validator_so_it_can_still_be_cloned()
    {
        using var db = new TestDb();
        var templateId = SeedTemplate(db);
        SeedLegacyVersion(db, templateId, 1, "{\"prompt\":\"ok?\",\"customEdges\":[\"Respond\"]}");
        SeedLegacyVersion(db, templateId, 2, "{\"prompt\":\"ok?\"}");
        db.Context.SaveChanges();

        await NodeOutputsMigrator.MigrateAsync(db.Context, TestContext.Current.CancellationToken);

        var mgr = new LoopTemplateManager(db.LoopTemplates);
        foreach (var versionNumber in new[] { 1, 2 })
        {
            var graph = await mgr.GetVersionGraphAsync(templateId, versionNumber);
            Assert.NotNull(graph);
            Assert.Empty(LoopTemplateValidator.Validate(graph!));
        }
    }
}
