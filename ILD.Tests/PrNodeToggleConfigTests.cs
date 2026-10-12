using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Data.DTOs;

namespace ILD.Tests;

/// <summary>
/// A PR node toggle written as anything but a JSON boolean: refused at save,
/// and never able to take the node's other fields down with it when read.
/// </summary>
public class PrNodeToggleConfigTests
{
    [Theory]
    [InlineData("\"false\"")]
    [InlineData("0")]
    [InlineData("\"no\"")]
    public void A_toggle_that_is_not_a_json_boolean_is_refused_at_save(string value)
    {
        foreach (var key in new[] { "attachIfExists", "AttachIfExists" })
            Assert.Contains(LoopTemplateValidator.Validate(GraphWithToggle(key, value)),
                e => e.Contains("pr") && e.Contains("attachIfExists must be true or false"));
    }

    private static LoopTemplateGraph GraphWithToggle(string key, string value)
    {
        var pr = new LoopNodeDto { Id = "pr", NodeType = "PR", Label = "pr" };
        using (var doc = System.Text.Json.JsonDocument.Parse(value))
            pr.Config[key] = doc.RootElement.Clone();
        var graph = new LoopTemplateGraph(Guid.NewGuid(),
            new() { new LoopNodeDto { Id = "s", NodeType = "Start", Label = "s" }, pr, new LoopNodeDto { Id = "c", NodeType = "Cleanup", Label = "c" } },
            new()
            {
                new LoopNodeEdgeDto { Id = "e1", SourceNodeId = "s", TargetNodeId = "pr", EdgeType = "OnSuccess" },
                new LoopNodeEdgeDto { Id = "e2", SourceNodeId = "pr", TargetNodeId = "c", EdgeType = "Custom", Name = "on_merged" },
            });

        return graph;
    }

    [Fact]
    public void A_config_that_does_not_parse_even_field_by_field_reads_as_empty_rather_than_throwing()
    {
        var cfg = NodeConfig.Parse<NodeConfig.Pr>("{\"prompt\":\"a\",\"prompt\":\"b\",\"createIfNoneExists\":\"x\"}");

        Assert.NotNull(cfg);
    }

    [Fact]
    public void A_malformed_toggle_leaves_the_nodes_other_fields_readable()
    {
        var cfg = NodeConfig.Parse<NodeConfig.Pr>(
            "{\"prompt\":\"Review it\",\"prDescriptionTemplate\":\"{{WorkItem.Title}}\",\"createIfNoneExists\":\"false\",\"attachIfExists\":false}");

        Assert.Equal("Review it", cfg.Prompt);
        Assert.Equal("{{WorkItem.Title}}", cfg.PrDescriptionTemplate);
        Assert.Null(cfg.CreateIfNoneExists);
        Assert.False(cfg.AttachIfExists);
    }
}
