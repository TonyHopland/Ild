using System.Linq;
using FluentAssertions;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Moq;

namespace ILD.Tests;

public class LoopTemplateValidationTests
{
    private static LoopNodeDto Node(string id, string type, string? promptTemplate = null)
    {
        var dto = new LoopNodeDto { Id = id, NodeType = type, Label = id };
        if (promptTemplate != null) dto.Config["promptTemplate"] = promptTemplate;
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

        result.Valid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
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

        result.Valid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
        result.Errors.Should().Contain(e => e.Contains("Bogus.Thing"));
        result.Errors.Should().Contain(e => e.Contains("Unknown placeholder"));
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

        result.Valid.Should().BeFalse();
        result.Errors.Should().HaveCount(2);
        result.Errors.Should().Contain(e => e.Contains("Bogus.One"));
        result.Errors.Should().Contain(e => e.Contains("Bogus.Two"));
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

        result.Valid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
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

        result.Valid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Start"));
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

        result.Valid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Cleanup"));
    }
}
