using System.Linq;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Moq;

namespace ILD.Tests;

public class LoopTemplateValidationTests
{
    private static LoopNodeDto Node(string id, string type, string? prompt = null)
    {
        var dto = new LoopNodeDto { Id = id, NodeType = type, Label = id };
        if (prompt != null) dto.Config["prompt"] = prompt;
        return dto;
    }

    private static LoopNodeEdgeDto Edge(string from, string to, string type = "OnSuccess")
        => new() { Id = $"{from}->{to}", SourceNodeId = from, TargetNodeId = to, EdgeType = type };

    [Fact]
    public async Task ValidateGraphAsync_returns_valid_true_for_graph_with_known_placeholders()
    {
        var manager = new LoopTemplateManager(Mock.Of<ILD.Data.Stores.Interfaces.ILoopTemplateStore>());

        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new[]
            {
                Node("s", "Start"),
                Node("a", "AI", "Title: {{WorkItem.Title}} Desc: {{WorkItem.Description}}"),
                Node("c", "Cleanup"),
            }.ToList(),
            new[] { Edge("s", "a"), Edge("a", "c") }.ToList());

        var result = await manager.ValidateGraphAsync(g);

        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateGraphAsync_returns_valid_false_with_specific_errors_for_unknown_placeholder()
    {
        var manager = new LoopTemplateManager(Mock.Of<ILD.Data.Stores.Interfaces.ILoopTemplateStore>());

        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new[]
            {
                Node("s", "Start"),
                Node("a", "AI", "Title: {{WorkItem.Title}} {{Bogus.Thing}}"),
                Node("c", "Cleanup"),
            }.ToList(),
            new[] { Edge("s", "a"), Edge("a", "c") }.ToList());

        var result = await manager.ValidateGraphAsync(g);

        Assert.False(result.Valid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("Bogus.Thing"));
        Assert.Contains(result.Errors, e => e.Contains("Unknown placeholder"));
    }

    [Fact]
    public async Task ValidateGraphAsync_returns_multiple_errors_for_multiple_unknown_placeholders()
    {
        var manager = new LoopTemplateManager(Mock.Of<ILD.Data.Stores.Interfaces.ILoopTemplateStore>());

        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new[]
            {
                Node("s", "Start"),
                Node("a", "AI", "{{Bogus.One}} {{Bogus.Two}}"),
                Node("c", "Cleanup"),
            }.ToList(),
            new[] { Edge("s", "a"), Edge("a", "c") }.ToList());

        var result = await manager.ValidateGraphAsync(g);

        Assert.False(result.Valid);
        Assert.Equal(2, result.Errors.Count());
        Assert.Contains(result.Errors, e => e.Contains("Bogus.One"));
        Assert.Contains(result.Errors, e => e.Contains("Bogus.Two"));
    }

    [Fact]
    public async Task ValidateGraphAsync_allows_WorkTree_File_prefix_placeholders()
    {
        var manager = new LoopTemplateManager(Mock.Of<ILD.Data.Stores.Interfaces.ILoopTemplateStore>());

        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new[]
            {
                Node("s", "Start"),
                Node("a", "AI", "File contents: {{WorkTree.File:src/main.cs}}"),
                Node("c", "Cleanup"),
            }.ToList(),
            new[] { Edge("s", "a"), Edge("a", "c") }.ToList());

        var result = await manager.ValidateGraphAsync(g);

        Assert.True(result.Valid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateGraphAsync_returns_error_without_start_node()
    {
        var manager = new LoopTemplateManager(Mock.Of<ILD.Data.Stores.Interfaces.ILoopTemplateStore>());

        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new[]
            {
                Node("a", "AI", "hello"),
                Node("c", "Cleanup"),
            }.ToList(),
            new[] { Edge("a", "c") }.ToList());

        var result = await manager.ValidateGraphAsync(g);

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, e => e.Contains("Start"));
    }

    [Fact]
    public async Task ValidateGraphAsync_returns_error_without_cleanup_node()
    {
        var manager = new LoopTemplateManager(Mock.Of<ILD.Data.Stores.Interfaces.ILoopTemplateStore>());

        var g = new LoopTemplateGraph(Guid.NewGuid(),
            new[]
            {
                Node("s", "Start"),
                Node("a", "AI", "hello"),
            }.ToList(),
            new[] { Edge("s", "a") }.ToList());

        var result = await manager.ValidateGraphAsync(g);

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, e => e.Contains("Cleanup"));
    }
}
