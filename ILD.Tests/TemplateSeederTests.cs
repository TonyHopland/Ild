using ILD.Api.Configuration;
using ILD.Core.Services.Implementations;
using ILD.Data.Enums;
using System.Text.Json;
using System.Text.Json.Nodes;
using ILD.Data;

namespace ILD.Tests;

public class TemplateSeederTests
{
    [Fact]
    public async Task SeedAsync_seeds_the_example_loops()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        await TemplateSeeder.SeedAsync(db.LoopTemplates, mgr);

        var templates = (await db.LoopTemplates.GetAllAsync()).ToList();
        var names = templates.Select(t => t.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Develop", "Prototype" }, names);
        Assert.DoesNotContain("AI-Assisted Feature", names);
    }

    [Fact]
    public async Task SeedAsync_carries_recovery_policy_and_pr_wiring_from_the_json()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        await TemplateSeeder.SeedAsync(db.LoopTemplates, mgr);

        var develop = (await db.LoopTemplates.GetAllAsync()).Single(t => t.Name == "Develop");
        Assert.Equal(RecoveryPolicy.AutoResume, develop.RecoveryPolicy);

        var graph = await mgr.GetVersionGraphAsync(develop.Id, 1);
        Assert.NotNull(graph);

        var pr = graph!.Nodes.Single(n => n.NodeType == "PR");
        var sendBackBrief = graph.Nodes.Single(n => n.Label == "Send Back Brief");
        var retroBrief = graph.Nodes.Single(n => n.Label == "Retro Brief");

        // The human's send_back button reaches the developer.
        Assert.Contains(graph.Edges, e =>
            e.SourceNodeId == pr.Id
            && e.TargetNodeId == sendBackBrief.Id
            && e.EdgeType == "Custom"
            && e.Name == "send_back");

        // A merged PR must reach the retro — without it the run would park forever (no fallback).
        Assert.Contains(graph.Edges, e =>
            e.SourceNodeId == pr.Id
            && e.TargetNodeId == retroBrief.Id
            && e.EdgeType == "Custom"
            && e.Name == "on_merged");

        // A PR-node failure must not loop back into the implementation session.
        Assert.DoesNotContain(graph.Edges, e => e.SourceNodeId == pr.Id && e.EdgeType == "OnFailure");
    }

    [Fact]
    public async Task SeedAsync_is_a_no_op_when_templates_already_exist()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        await TemplateSeeder.SeedAsync(db.LoopTemplates, mgr);
        var firstCount = (await db.LoopTemplates.GetAllAsync()).Count();

        await TemplateSeeder.SeedAsync(db.LoopTemplates, mgr);
        var secondCount = (await db.LoopTemplates.GetAllAsync()).Count();

        Assert.Equal(firstCount, secondCount);
    }

    [Theory]
    [InlineData("Develop.json")]
    [InlineData("Prototype.json")]
    public void Example_loop_is_a_v2_document_the_upgrader_has_nothing_left_to_add_to(string file)
    {
        // The example loops are the seed templates: a name they route by but do not
        // declare would be refused at seed time or lost from the editor.
        var original = JsonNode.Parse(RepositoryFiles.ReadAllText(Path.Combine("example-loops", file)))!.AsObject();
        Assert.Equal("ild-loop-template/v2", (string)original["$schema"]!);

        var asV1 = original.DeepClone().AsObject();
        asV1["$schema"] = "ild-loop-template/v1";
        var reupgraded = JsonNode.Parse(LoopDocumentUpgrader.Upgrade(asV1.ToJsonString()))!.AsObject();

        foreach (var node in original["nodes"]!.AsArray())
        {
            var config = node!["config"]!.AsObject();
            Assert.False(config.ContainsKey("customEdges"), $"{file}: node '{node["label"]}' still sets customEdges");
            var again = reupgraded["nodes"]!.AsArray().Single(n => (string)n!["id"]! == (string)node["id"]!)!["config"];
            Assert.True(JsonNode.DeepEquals(config, again),
                $"{file}: node '{node["label"]}' is missing outputs: {config.ToJsonString()} vs {again!.ToJsonString()}");
        }
    }
}
