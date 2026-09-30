using System.Text.Json.Nodes;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Migrations;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

public class LoopDocumentUpgraderTests
{
    private static readonly string[] Reserved =
    {
        "on_rejected", "on_merge_conflict", "on_ci_failed", "on_comment",
        "on_approved", "on_ci_passed", "on_merged", "on_abandoned",
    };

    private static JsonObject NodeJson(string id, string type, string config)
        => new() { ["id"] = id, ["type"] = type, ["label"] = id, ["config"] = JsonNode.Parse(config), ["x-node-note"] = "kept" };

    private static JsonObject EdgeJson(string id, string from, string to, string type, string? name = null)
        => new() { ["id"] = id, ["sourceNodeId"] = from, ["targetNodeId"] = to, ["edgeType"] = type, ["name"] = name };

    /// <summary>
    /// A document as ILD exported it before outputs existed (or as an AI still
    /// writes it): output names in customEdges, matchRules and cases/defaultEdge,
    /// and one Human output that exists only as an edge.
    /// </summary>
    private static JsonObject LegacyDocument(string? schema = "ild-loop-template/v1")
    {
        var doc = new JsonObject();
        if (schema != null) doc["$schema"] = schema;
        doc["name"] = "Review Loop";
        doc["description"] = "";
        doc["recoveryPolicy"] = "AutoResume";
        doc["x-doc-note"] = "kept";
        doc["nodes"] = new JsonArray(
            NodeJson("start", "Start", "{\"createWorktree\":true,\"__pos\":{\"x\":1,\"y\":2}}"),
            NodeJson("review", "Human", "{\"prompt\":\"ok?\",\"customEdges\":[\"Respond\",\"\"]}"),
            NodeJson("ai", "AI", "{\"prompt\":\"Review it\",\"matchRules\":[{\"pattern\":\"REJECT\",\"edgeName\":\"Reject\"}]}"),
            NodeJson("gate", "Condition", "{\"cases\":[{\"variant\":\"PrExists\",\"edgeName\":\" has-pr \"}],\"defaultEdge\":\"otherwise \"}"),
            NodeJson("pr", "PR", "{\"prDescriptionTemplate\":\"t\",\"customEdges\":[\"deploy\"]}"),
            NodeJson("cleanup", "Cleanup", "{}"));
        doc["edges"] = new JsonArray(
            EdgeJson("e1", "start", "review", "OnSuccess"),
            EdgeJson("e2", "review", "ai", "OnSuccess"),
            EdgeJson("e3", "review", "ai", "Custom", "Respond"),
            EdgeJson("e4", "review", "cleanup", "Custom", "Escalate"),
            EdgeJson("e5", "ai", "gate", "OnSuccess"),
            EdgeJson("e6", "ai", "review", "Custom", "Reject"),
            EdgeJson("e7", "gate", "pr", "Custom", "has-pr"),
            EdgeJson("e8", "gate", "cleanup", "Custom", "otherwise"),
            EdgeJson("e9", "pr", "cleanup", "Custom", "on_merged"),
            EdgeJson("e10", "pr", "cleanup", "Custom", "deploy"));
        return doc;
    }

    private static JsonObject Upgraded(JsonObject legacy)
        => Assert.IsType<JsonObject>(JsonNode.Parse(LoopDocumentUpgrader.Upgrade(legacy.ToJsonString())));

    private static JsonObject NodeConfig(JsonObject doc, string id)
        => Assert.IsType<JsonObject>(doc["nodes"]!.AsArray().Single(n => (string)n!["id"]! == id)!["config"]);

    private static List<string> SortedOutputNames(JsonObject config)
        => config["outputs"]!.AsArray().Select(o => (string)o!["name"]!).OrderBy(n => n, StringComparer.Ordinal).ToList();

    [Theory]
    [InlineData("ild-loop-template/v1")]
    [InlineData(null)]
    public void A_v1_document_becomes_v2_with_every_output_declared(string? schema)
    {
        var legacy = LegacyDocument(schema);

        var doc = Upgraded(legacy);

        Assert.Equal("ild-loop-template/v2", (string)doc["$schema"]!);
        var expected = new Dictionary<string, string[]>
        {
            ["start"] = new[] { "OnSuccess", "OnFailure" },
            ["review"] = new[] { "OnSuccess", "OnFailure", "Respond", "Escalate" },
            ["ai"] = new[] { "OnSuccess", "OnFailure", "Reject" },
            ["gate"] = new[] { "OnFailure", "has-pr", "otherwise" },
            ["pr"] = new[] { "OnSuccess", "OnFailure", "deploy" }.Concat(Reserved).ToArray(),
            ["cleanup"] = Array.Empty<string>(),
        };
        foreach (var (id, names) in expected)
        {
            var config = NodeConfig(doc, id);
            Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), SortedOutputNames(config));
            Assert.False(config.ContainsKey("customEdges"), $"{id} still carries customEdges");
        }

        var prOutputs = NodeConfig(doc, "pr")["outputs"]!.AsArray();
        Assert.All(prOutputs, o => Assert.Equal(Reserved.Contains((string)o!["name"]!), o!["reserved"]?.GetValue<bool>() ?? false));
    }

    [Fact]
    public void Upgrading_keeps_the_edges_and_every_field_it_does_not_convert()
    {
        var legacy = LegacyDocument();

        var doc = Upgraded(legacy);

        Assert.True(JsonNode.DeepEquals(legacy["edges"], doc["edges"]));
        Assert.Equal("kept", (string)doc["x-doc-note"]!);
        Assert.Equal("Review Loop", (string)doc["name"]!);
        Assert.All(doc["nodes"]!.AsArray(), n => Assert.Equal("kept", (string)n!["x-node-note"]!));
        var start = NodeConfig(doc, "start");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"x\":1,\"y\":2}"), start["__pos"]));
        Assert.True((bool)start["createWorktree"]!);
        Assert.Equal(" has-pr ", (string)NodeConfig(doc, "gate")["cases"]![0]!["edgeName"]!);
    }

    [Fact]
    public void Upgrading_twice_changes_nothing_the_second_time()
    {
        var once = LoopDocumentUpgrader.Upgrade(LegacyDocument().ToJsonString());

        Assert.Equal(once, LoopDocumentUpgrader.Upgrade(once));
    }

    [Fact]
    public void An_output_already_declared_in_a_v1_document_keeps_its_unknown_fields()
    {
        var legacy = LegacyDocument();
        NodeConfig(legacy, "review")["outputs"] = JsonNode.Parse("[{\"name\":\"Respond\",\"visible\":false,\"color\":\"x\"}]");

        var outputs = NodeConfig(Upgraded(legacy), "review")["outputs"]!.AsArray();

        Assert.Single(outputs, o => (string)o!["name"]! == "Respond");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"name\":\"Respond\",\"visible\":false,\"color\":\"x\"}"), outputs[0]));
    }

    [Theory]
    [InlineData("{\n  \"$schema\": \"ild-loop-template/v2\",\n  \"name\": \"L\",\n  \"nodes\": [ { \"id\": \"h\", \"type\": \"Human\", \"config\": { } } ],\n  \"edges\": [ ]\n}")]
    [InlineData("{\"$schema\":\"ild-loop-template/v0\",\"name\":\"L\",\"nodes\":[{\"id\":\"h\",\"type\":\"Human\",\"config\":{\"customEdges\":[\"a\"]}}],\"edges\":[]}")]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    public void Anything_that_is_not_a_v1_document_is_returned_unchanged(string input)
    {
        Assert.Equal(input, LoopDocumentUpgrader.Upgrade(input));
    }

    [Fact]
    public async Task The_document_upgrader_and_the_startup_migrator_convert_a_node_the_same_way()
    {
        using var db = new TestDb();
        var legacy = LegacyDocument();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "Review Loop", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);

        var ids = new Dictionary<string, Guid>();
        foreach (var node in legacy["nodes"]!.AsArray())
        {
            var id = Guid.NewGuid();
            ids[(string)node!["id"]!] = id;
            db.Context.LoopNodes.Add(new LoopNode
            {
                Id = id, LoopTemplateVersionId = version.Id, Label = (string)node["label"]!,
                NodeType = Enum.Parse<NodeType>((string)node["type"]!),
                Config = node["config"]!.ToJsonString(),
            });
        }
        foreach (var edge in legacy["edges"]!.AsArray())
        {
            db.Context.LoopNodeEdges.Add(new LoopNodeEdge
            {
                Id = Guid.NewGuid(),
                SourceNodeId = ids[(string)edge!["sourceNodeId"]!],
                TargetNodeId = ids[(string)edge["targetNodeId"]!],
                EdgeType = Enum.Parse<EdgeType>((string)edge["edgeType"]!),
                Name = (string?)edge["name"],
            });
        }
        db.Context.SaveChanges();

        var doc = Upgraded(legacy);
        await NodeOutputsMigrator.MigrateAsync(db.Context, TestContext.Current.CancellationToken);

        var fresh = db.Fresh();
        foreach (var (docId, nodeId) in ids)
        {
            var stored = Assert.IsType<JsonObject>(JsonNode.Parse(fresh.LoopNodes.Single(n => n.Id == nodeId).Config!));
            Assert.True(JsonNode.DeepEquals(NodeConfig(doc, docId), stored),
                $"{docId}: document {NodeConfig(doc, docId).ToJsonString()} vs migrated {stored.ToJsonString()}");
        }
    }
}
