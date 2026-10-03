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
        Assert.Equal(new[] { "Advanced", "Simple" }, names);

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

        var advanced = (await db.LoopTemplates.GetAllAsync()).Single(t => t.Name == "Advanced");
        Assert.Equal(RecoveryPolicy.AutoResume, advanced.RecoveryPolicy);

        var graph = await mgr.GetVersionGraphAsync(advanced.Id, 1);
        Assert.NotNull(graph);

        var review = graph!.Nodes.Single(n => n.Label == "Reviewer");
        Assert.Equal("AI", review.NodeType);
        Assert.Equal("{{PreviousNode.Output}}", ReadString(review.Config, "prompt"));
        Assert.Contains(graph.Edges, e => e.SourceNodeId == review.Id && e.Name == "changes_requested");

        // The Start node's worktree flags survive the round-trip.
        var start = graph.Nodes.Single(n => n.NodeType == "Start");
        Assert.True(ReadBool(start.Config, "createWorktree"));
    }

    [Theory]
    [InlineData("Advanced")]
    [InlineData("Simple")]
    public async Task Example_loop_wires_on_merged_to_cleanup_and_does_not_loop_pr_failure_back(string name)
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        await TemplateSeeder.SeedAsync(db.LoopTemplates, mgr);

        var template = (await db.LoopTemplates.GetAllAsync()).Single(t => t.Name == name);
        var graph = await mgr.GetVersionGraphAsync(template.Id, 1);
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
    [InlineData("Advanced.json")]
    [InlineData("Simple.json")]
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
