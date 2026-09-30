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
        Assert.Equal(new[] { "Development", "DevTeam", "Plan", "Q&A" }, names);

        // The pre-example seed loops must no longer be created.
        Assert.DoesNotContain("Simple Code Change", names);
        Assert.DoesNotContain("AI-Assisted Feature", names);
    }

    [Fact]
    public async Task SeedAsync_carries_node_config_and_recovery_policy_from_the_json()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        await TemplateSeeder.SeedAsync(db.LoopTemplates, mgr);

        var development = (await db.LoopTemplates.GetAllAsync()).Single(t => t.Name == "Development");
        Assert.Equal(RecoveryPolicy.AutoResume, development.RecoveryPolicy);

        var graph = await mgr.GetVersionGraphAsync(development.Id, 1);
        Assert.NotNull(graph);

        // The strict reviewer keeps its named Reject custom edge wired to a match rule.
        var review = graph!.Nodes.Single(n => n.Label == "Strict Code Review");
        Assert.Equal("AI", review.NodeType);
        Assert.Contains("strict, independent code reviewer", ReadString(review.Config, "prompt"));
        Assert.Contains(graph.Edges, e => e.SourceNodeId == review.Id && e.Name == "Reject");

        // The Start node's worktree flags survive the round-trip.
        var start = graph.Nodes.Single(n => n.NodeType == "Start");
        Assert.True(ReadBool(start.Config, "createWorktree"));
    }

    [Fact]
    public async Task Development_loop_wires_on_merged_to_cleanup_and_does_not_loop_pr_failure_back()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        await TemplateSeeder.SeedAsync(db.LoopTemplates, mgr);

        var development = (await db.LoopTemplates.GetAllAsync()).Single(t => t.Name == "Development");
        var graph = await mgr.GetVersionGraphAsync(development.Id, 1);
        Assert.NotNull(graph);

        var pr = graph!.Nodes.Single(n => n.NodeType == "PR");
        var cleanup = graph.Nodes.Single(n => n.NodeType == "Cleanup");

        // The heartbeat's on_merged edge must reach Cleanup — without it, a
        // merged PR would leave the run parked forever (no fallback).
        Assert.Contains(graph.Edges, e =>
            e.SourceNodeId == pr.Id
            && e.TargetNodeId == cleanup.Id
            && e.EdgeType == "Custom"
            && e.Name == "on_merged");

        // A PR-node failure (e.g. a 401 the AI cannot fix) must NOT loop back
        // into the implementation session; the OnFailure edge is removed so the
        // run simply fails instead of looping endlessly.
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
    [InlineData("DevTeam.json")]
    [InlineData("Development.json")]
    [InlineData("Plan.json")]
    [InlineData("Q&A.json")]
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

    private static bool ReadBool(Dictionary<string, object> config, string key)
    {
        return config[key] switch
        {
            bool value => value,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => throw new InvalidOperationException($"Config key '{key}' is not a boolean.")
        };
    }

    private static string? ReadString(Dictionary<string, object> config, string key)
    {
        return config[key] switch
        {
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } value => value.GetString(),
            _ => throw new InvalidOperationException($"Config key '{key}' is not a string.")
        };
    }
}
