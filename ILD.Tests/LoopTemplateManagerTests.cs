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
                new LoopNodeDto { Id = "h", NodeType = "Human", Label = "Review", Config = new() { ["prompt"] = "ok?", ["outputs"] = new List<Dictionary<string, object>> { new() { ["name"] = "Respond" } } } },
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

    // ---- declared outputs on save ---------------------------------------------

    private static readonly string[] Reserved =
    {
        "on_rejected", "on_merge_conflict", "on_ci_failed", "on_comment",
        "on_approved", "on_ci_passed", "on_merged", "on_abandoned",
    };

    /// <summary>A node config as a request body delivers it: parsed JSON values.</summary>
    private static Dictionary<string, object> Cfg(string json)
        => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;

    private static LoopNodeDto N(string id, string type, string config = "{}")
        => new() { Id = id, NodeType = type, Label = id, Config = Cfg(config) };

    private static LoopNodeEdgeDto E(string from, string to, string type = "OnSuccess", string? name = null)
        => new() { Id = $"{from}-{to}-{type}-{name}", SourceNodeId = from, TargetNodeId = to, EdgeType = type, Name = name };

    /// <summary>The stored config of each node of one version, by label, exactly as persisted.</summary>
    private static async Task<Dictionary<string, string>> StoredConfigs(TestDb db, Guid templateId, int versionNumber)
    {
        var fresh = db.Fresh();
        var version = await fresh.LoopTemplateVersions
            .SingleAsync(v => v.LoopTemplateId == templateId && v.VersionNumber == versionNumber, TestContext.Current.CancellationToken);
        return await fresh.LoopNodes.Where(n => n.LoopTemplateVersionId == version.Id)
            .ToDictionaryAsync(n => n.Label, n => n.Config!, TestContext.Current.CancellationToken);
    }

    private static System.Text.Json.Nodes.JsonObject Obj(string json)
        => (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!;

    private static List<System.Text.Json.Nodes.JsonObject> OutputsOf(string config)
        => Obj(config)["outputs"]!.AsArray().Select(o => (System.Text.Json.Nodes.JsonObject)o!).ToList();

    private static List<string> NamesOf(string config) => OutputsOf(config).Select(o => (string)o["name"]!).ToList();

    /// <summary>Every node type, none of them declaring its fixed or reserved outputs.</summary>
    private static LoopTemplateGraph AllTypesGraph(string prConfig = "{}", string humanConfig = "{\"prompt\":\"ok?\"}") => new(
        Guid.Empty,
        new()
        {
            N("s", "Start"),
            N("build", "Cmd", "{\"command\":\"echo hi\"}"),
            N("ai", "AI", "{\"prompt\":\"Review it\"}"),
            N("h", "Human", humanConfig),
            N("p", "Prompt", "{\"prompt\":\"Go on\"}"),
            N("pr", "PR", prConfig),
            N("gate", "Condition",
                "{\"cases\":[{\"variant\":\"PrExists\",\"edgeName\":\"has-pr\"}],\"defaultEdge\":\"otherwise\",\"outputs\":[{\"name\":\"has-pr\"},{\"name\":\"otherwise\"}]}"),
            N("c", "Cleanup"),
        },
        new()
        {
            E("s", "build"), E("build", "ai"), E("ai", "h"), E("h", "p"), E("p", "pr"),
            E("pr", "gate", "Custom", "on_merged"),
            E("gate", "c", "Custom", "has-pr"), E("gate", "c", "Custom", "otherwise"),
        });

    [Fact]
    public async Task Save_fills_in_the_fixed_and_reserved_outputs_of_every_node_type()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        var id = await mgr.CreateLoopTemplateAsync("outputs", "", AllTypesGraph());

        var configs = await StoredConfigs(db, id, 1);
        foreach (var label in new[] { "s", "build", "ai", "h", "p", "pr" })
        {
            Assert.Contains("OnSuccess", NamesOf(configs[label]));
            Assert.Contains("OnFailure", NamesOf(configs[label]));
        }
        Assert.Contains("OnFailure", NamesOf(configs["gate"]));
        Assert.DoesNotContain("OnSuccess", NamesOf(configs["gate"]));
        Assert.Empty(NamesOf(configs["c"]));

        foreach (var name in Reserved)
        {
            var output = Assert.Single(OutputsOf(configs["pr"]), o => (string)o["name"]! == name);
            Assert.True((bool)output["reserved"]!);
        }
        foreach (var (label, config) in configs)
        {
            Assert.All(OutputsOf(config).Where(o => !(label == "pr" && Reserved.Contains((string)o["name"]!))),
                o => Assert.False(o.ContainsKey("reserved"), $"{label}: {o["name"]} is marked reserved"));
            Assert.False(Obj(config).ContainsKey("customEdges"));
        }
    }

    [Fact]
    public async Task Save_keeps_every_other_key_and_unknown_output_fields_in_place()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);
        var graph = AllTypesGraph(
            humanConfig: "{\"prompt\":\"ok?\",\"outputs\":[{\"name\":\"approve\",\"visible\":false,\"color\":\"x\"}],\"inputLabel\":\"Why?\"}");
        graph.Edges.Add(E("h", "c", "Custom", "approve"));

        var id = await mgr.CreateLoopTemplateAsync("outputs", "", graph);

        var human = Obj((await StoredConfigs(db, id, 1))["h"]);
        Assert.Equal(new[] { "prompt", "outputs", "inputLabel" }, human.Select(p => p.Key));
        var first = (System.Text.Json.Nodes.JsonObject)human["outputs"]![0]!;
        Assert.Equal(new[] { "name", "visible", "color" }, first.Select(p => p.Key));
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(Obj("{\"name\":\"approve\",\"visible\":false,\"color\":\"x\"}"), first));
    }

    [Fact]
    public async Task Save_brings_back_a_reserved_or_fixed_output_that_was_removed_or_renamed()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);
        // on_merged renamed to "merged" (still flagged reserved), the other reserved
        // outputs and OnSuccess dropped; on_ci_failed kept with a setting.
        var graph = AllTypesGraph(
            prConfig: "{\"outputs\":[{\"name\":\"merged\",\"reserved\":true},{\"name\":\"on_ci_failed\",\"visible\":false},{\"name\":\"OnFailure\"}]}",
            humanConfig: "{\"prompt\":\"ok?\",\"outputs\":[{\"name\":\"Done\"}]}");

        var id = await mgr.CreateLoopTemplateAsync("outputs", "", graph);

        var configs = await StoredConfigs(db, id, 1);
        var pr = OutputsOf(configs["pr"]);
        foreach (var name in Reserved.Append("OnSuccess").Append("OnFailure"))
            Assert.Single(pr, o => (string)o["name"]! == name);
        var merged = Assert.Single(pr, o => (string)o["name"]! == "merged");
        Assert.False(merged.ContainsKey("reserved"));
        var ciFailed = pr.Single(o => (string)o["name"]! == "on_ci_failed");
        Assert.False((bool)ciFailed["visible"]!);
        Assert.True((bool)ciFailed["reserved"]!);
        Assert.Equal("merged", (string)pr[0]["name"]!);

        Assert.Equal(new[] { "Done", "OnFailure", "OnSuccess" }, NamesOf(configs["h"]).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Save_refuses_a_config_that_still_carries_customEdges(bool wired)
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);
        var graph = AllTypesGraph(
            prConfig: "{\"customEdges\":[\"deploy\"]}",
            humanConfig: "{\"prompt\":\"ok?\",\"customEdges\":[\"Respond\"],\"inputLabel\":\"Why?\"}");
        if (wired)
        {
            graph.Edges.Add(E("h", "c", "Custom", "Respond"));
            graph.Edges.Add(E("pr", "c", "Custom", "deploy"));
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => mgr.CreateLoopTemplateAsync("outputs", "", graph));

        Assert.Contains("Node h has customEdges, which is no longer supported; declare outputs in config.outputs", ex.Message);
        Assert.Contains("Node pr has customEdges, which is no longer supported; declare outputs in config.outputs", ex.Message);
        Assert.Empty(await db.Fresh().LoopTemplates.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Saving_the_same_graph_again_and_cloning_it_store_identical_config()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);
        var graph = AllTypesGraph(
            prConfig: "{\"prDescriptionTemplate\":\"t\",\"outputs\":[{\"name\":\"deploy\",\"color\":\"x\"}]}",
            humanConfig: "{\"prompt\":\"ok?\",\"outputs\":[{\"name\":\"approve\",\"visible\":false}]}");
        graph.Edges.Add(E("h", "c", "Custom", "approve"));

        var id = await mgr.CreateLoopTemplateAsync("outputs", "", graph);
        var saved = await mgr.GetVersionGraphAsync(id, 1);
        await mgr.UpdateLoopTemplateAsync(id, "outputs", "", saved!);
        var cloneId = await mgr.CloneLoopTemplateAsync(id, "outputs copy");

        var first = await StoredConfigs(db, id, 1);
        Assert.Equal(first, await StoredConfigs(db, id, 2));
        Assert.Equal(first, await StoredConfigs(db, cloneId, 1));
        Assert.Contains(OutputsOf(first["h"]), o => (string)o["name"]! == "approve" && (bool)o["visible"]! == false);
        Assert.Contains(OutputsOf(first["pr"]), o => (string)o["name"]! == "deploy" && (string)o["color"]! == "x");
    }

    /// <summary>The outputs of a stored config that carry a <c>visible</c> key, with its value.</summary>
    private static Dictionary<string, bool> VisibleOf(string config)
        => OutputsOf(config).Where(o => o.ContainsKey("visible"))
            .ToDictionary(o => (string)o["name"]!, o => (bool)o["visible"]!);

    [Fact]
    public async Task Visible_on_any_output_is_stored_as_written_and_never_added_through_save_new_version_and_clone()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);
        var graph = AllTypesGraph(
            prConfig: "{\"outputs\":[{\"name\":\"on_merged\",\"reserved\":true,\"visible\":true},{\"name\":\"on_ci_failed\",\"visible\":false},{\"name\":\"OnSuccess\",\"visible\":true}]}",
            humanConfig: "{\"prompt\":\"ok?\",\"outputs\":[{\"name\":\"OnSuccess\",\"visible\":false},{\"name\":\"OnFailure\",\"visible\":false},{\"name\":\"later\",\"visible\":false}]}");
        graph.Nodes.Single(n => n.Id == "build").Config =
            Cfg("{\"command\":\"echo hi\",\"outputs\":[{\"name\":\"OnFailure\",\"visible\":false}]}");
        graph.Edges.Add(E("h", "c", "Custom", "later"));
        var expected = new Dictionary<string, Dictionary<string, bool>>
        {
            ["pr"] = new() { ["on_merged"] = true, ["on_ci_failed"] = false, ["OnSuccess"] = true },
            ["h"] = new() { ["OnSuccess"] = false, ["OnFailure"] = false, ["later"] = false },
            ["build"] = new() { ["OnFailure"] = false },
        };

        var id = await mgr.CreateLoopTemplateAsync("outputs", "", graph);
        var reloaded = await mgr.GetVersionGraphAsync(id, 1);
        await mgr.UpdateLoopTemplateAsync(id, "outputs", "", reloaded!);
        var cloneId = await mgr.CloneLoopTemplateAsync(id, "outputs copy");

        foreach (var node in reloaded!.Nodes)
            Assert.Equal(
                expected.GetValueOrDefault(node.Label, new()),
                VisibleOf(System.Text.Json.JsonSerializer.Serialize(node.Config)));
        foreach (var (templateId, version) in new[] { (id, 1), (id, 2), (cloneId, 1) })
        {
            var configs = await StoredConfigs(db, templateId, version);
            Assert.Equal(8, configs.Count);
            foreach (var (label, config) in configs)
                Assert.Equal(expected.GetValueOrDefault(label, new()), VisibleOf(config));
        }
    }
}
