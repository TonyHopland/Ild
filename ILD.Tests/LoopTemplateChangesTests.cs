using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;

namespace ILD.Tests;

/// <summary>
/// Edges are compared the same way whether two loops are checked for being the
/// same loop or diffed for review: by (source, target, type, name), ignoring edge
/// ids and order, a blank name counting as none, the type in any case, and each
/// edge counted as often as it appears.
/// </summary>
public class LoopTemplateChangesTests
{
    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";
    private const string C = "33333333-3333-3333-3333-333333333333";

    private static string Edge(string id, string source, string target, string type = "OnSuccess", string? name = null)
        => $$"""{"id":"{{id}}","sourceNodeId":"{{source}}","targetNodeId":"{{target}}","edgeType":"{{type}}","name":{{(name is null ? "null" : $"\"{name}\"")}}}""";

    private static string Loop(params string[] edges)
        => $$$"""
           {"$schema":"ild-loop-template/v2","name":"Dev Loop","description":"","recoveryPolicy":"AutoResume",
            "nodes":[
              {"id":"{{{A}}}","type":"Start","label":"Start","config":{}},
              {"id":"{{{B}}}","type":"Cmd","label":"build","config":{"command":"make"}},
              {"id":"{{{C}}}","type":"Cleanup","label":"Cleanup","config":{}}],
            "edges":[{{{string.Join(",", edges)}}}]}
           """;

    public static TheoryData<string, string, bool> EdgeComparisons => new()
    {
        { Loop(Edge("e1", A, B), Edge("e2", B, C)), Loop(Edge("x2", B, C), Edge("x1", A, B)), true },
        { Loop(Edge("e1", A, B, name: "")), Loop(Edge("e1", A, B, name: null)), true },
        { Loop(Edge("e1", A, B, name: "  ")), Loop(Edge("e1", A, B)), true },
        { Loop(Edge("e1", A, B, type: "onsuccess")), Loop(Edge("e1", A, B, type: "OnSuccess")), true },
        { Loop(Edge("e1", A, B, name: "retry")), Loop(Edge("e1", A, B)), false },
        { Loop(Edge("e1", A, B, type: "OnFailure")), Loop(Edge("e1", A, B)), false },
        { Loop(Edge("e1", A, B), Edge("e2", A, B)), Loop(Edge("e1", A, B)), false },
        { Loop(Edge("e1", A, B), Edge("e2", A, B), Edge("e3", B, C)), Loop(Edge("e1", A, B), Edge("e3", B, C), Edge("e4", B, C)), false },
        { Loop(Edge("e1", A, B), Edge("e2", A, B), Edge("e3", B, C)), Loop(Edge("e3", B, C), Edge("x", A, B), Edge("y", A, B)), true },
    };

    [Theory]
    [MemberData(nameof(EdgeComparisons))]
    public void Two_loops_are_the_same_only_with_the_same_edges_each_as_often(string document, string other, bool same)
    {
        Assert.Equal(same, LoopTemplateChanges.Equivalent(document, other));
        Assert.Equal(same, LoopTemplateChanges.Equivalent(other, document));
    }

    [Fact]
    public void A_diff_counts_a_repeated_edge_as_often_as_it_appears()
    {
        var baseGraph = Base((A, B, EdgeType.OnSuccess, null), (A, B, EdgeType.OnSuccess, null), (B, C, EdgeType.OnSuccess, null));

        var fewer = LoopTemplateChanges.Diff(baseGraph, Loop(Edge("e1", A, B), Edge("e2", B, C)));
        var more = LoopTemplateChanges.Diff(baseGraph, Loop(Edge("e1", A, B, type: "onsuccess"), Edge("e2", A, B, name: ""), Edge("e3", A, B), Edge("e4", B, C)));
        var same = LoopTemplateChanges.Diff(baseGraph, Loop(Edge("x", B, C), Edge("y", A, B), Edge("z", A, B, name: " ")));

        Assert.Equal([new LoopEdgeRef("Start", "build", "OnSuccess", null)], fewer.EdgesRemoved);
        Assert.Empty(fewer.EdgesAdded);
        Assert.Equal([new LoopEdgeRef("Start", "build", "OnSuccess", null)], more.EdgesAdded);
        Assert.Empty(more.EdgesRemoved);
        Assert.Empty(same.EdgesAdded);
        Assert.Empty(same.EdgesRemoved);
    }

    private static TemplateGraph Base(params (string Source, string Target, EdgeType Type, string? Name)[] edges)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "Dev Loop", Description = "", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var nodes = new List<LoopNode>
        {
            new() { Id = Guid.Parse(A), NodeType = NodeType.Start, Label = "Start", Config = "{}" },
            new() { Id = Guid.Parse(B), NodeType = NodeType.Cmd, Label = "build", Config = """{"command":"make"}""" },
            new() { Id = Guid.Parse(C), NodeType = NodeType.Cleanup, Label = "Cleanup", Config = "{}" },
        };
        return new TemplateGraph(template, version, nodes, edges.Select(e => new LoopNodeEdge
        {
            Id = Guid.NewGuid(),
            SourceNodeId = Guid.Parse(e.Source),
            TargetNodeId = Guid.Parse(e.Target),
            EdgeType = e.Type,
            Name = e.Name,
        }).ToList());
    }
}
