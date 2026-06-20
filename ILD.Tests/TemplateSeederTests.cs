using ILD.Api.Configuration;
using ILD.Core.Services.Implementations;
using ILD.Data.Enums;
using System.Text.Json;

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
        Assert.Equal(new[] { "Development", "Plan", "Q&A" }, names);

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
