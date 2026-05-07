using FluentAssertions;
using ILD.Data.DTOs;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class LoopTemplateValidatorTests
{
    private static LoopNodeDto Node(string id, string type, string? initialPrompt = null)
    {
        var dto = new LoopNodeDto { Id = id, NodeType = type, Label = id };
        if (initialPrompt != null) dto.Config["initialPrompt"] = initialPrompt;
        return dto;
    }

    private static LoopNodeEdgeDto Edge(string from, string to, string type = "OnSuccess")
        => new() { Id = $"{from}->{to}", SourceNodeId = from, TargetNodeId = to, EdgeType = type };

    [Fact]
    public void Graph_without_start_node_is_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("a", "Cmd"), Node("c", "Cleanup") },
            new() { Edge("a", "c") });
        var errs = LoopTemplateValidator.Validate(g);
        errs.Should().Contain(e => e.Contains("Start"));
    }

    [Fact]
    public void Graph_without_cleanup_node_is_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("a", "Cmd") },
            new() { Edge("s", "a") });
        var errs = LoopTemplateValidator.Validate(g);
        errs.Should().Contain(e => e.Contains("Cleanup"));
    }

    [Fact]
    public void Unreachable_node_is_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("a", "Cmd"), Node("orphan", "Cmd"), Node("c", "Cleanup") },
            new() { Edge("s", "a"), Edge("a", "c") });
        var errs = LoopTemplateValidator.Validate(g);
        errs.Should().Contain(e => e.ToLower().Contains("unreachable"));
    }

    [Fact]
    public void No_path_to_cleanup_is_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("a", "Cmd"), Node("c", "Cleanup") },
            new() { Edge("s", "a"), Edge("a", "s") }); // cleanup unreachable
        var errs = LoopTemplateValidator.Validate(g);
        errs.Should().Contain(e => e.ToLower().Contains("cleanup"));
    }

    [Fact]
    public void Two_outgoing_edges_of_same_type_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("a", "Cmd"), Node("b", "Cmd"), Node("c", "Cleanup") },
            new() { Edge("s", "a"), Edge("s", "b"), Edge("a", "c"), Edge("b", "c") });
        var errs = LoopTemplateValidator.Validate(g);
        errs.Should().Contain(e => e.ToLower().Contains("duplicate"));
    }

    [Fact]
    public void Unknown_placeholder_in_prompt_template_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() {
                Node("s", "Start"),
                Node("a", "AI", "Title: {{WorkItem.Title}} {{Bogus.Thing}}"),
                Node("c", "Cleanup")
            },
            new() { Edge("s", "a"), Edge("a", "c") });
        var errs = LoopTemplateValidator.Validate(g);
        errs.Should().Contain(e => e.Contains("Bogus"));
    }

    [Fact]
    public void Valid_minimal_graph_passes()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("a", "Cmd"), Node("c", "Cleanup") },
            new() { Edge("s", "a"), Edge("a", "c") });
        var errs = LoopTemplateValidator.Validate(g);
        errs.Should().BeEmpty();
    }

    [Fact]
    public void OnRespond_edge_on_non_human_node_is_invalid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() { Node("s", "Start"), Node("a", "Cmd"), Node("c", "Cleanup") },
            new() { Edge("s", "a"), Edge("a", "c", "OnRespond") });
        var errs = LoopTemplateValidator.Validate(g);
        errs.Should().Contain(e => e.Contains("OnRespond"));
    }

    [Fact]
    public void OnRespond_edge_on_human_node_is_valid()
    {
        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new() {
                Node("s", "Start"),
                Node("h", "Human", "Review this"),
                Node("a", "AI"),
                Node("c", "Cleanup")
            },
            new() {
                Edge("s", "h"),
                Edge("h", "a", "OnRespond"),
                Edge("h", "a", "OnSuccess"),
                Edge("h", "c", "OnFailure"),
                Edge("a", "c")
            });
        var errs = LoopTemplateValidator.Validate(g);
        errs.Should().BeEmpty();
    }
}
