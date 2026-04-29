using FluentAssertions;
using ILD.Data.DTOs;
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

        var versions = await db.Context.LoopTemplateVersions.Where(v => v.LoopTemplateId == id).ToListAsync();
        versions.Should().HaveCount(1);
        versions[0].VersionNumber.Should().Be(1);
        (await db.Context.LoopNodes.CountAsync()).Should().Be(3);
        (await db.Context.LoopNodeEdges.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Create_invalid_graph_throws()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        var bad = new LoopTemplateGraph(Guid.Empty, new() { new LoopNodeDto { Id = "x", NodeType = "Cmd", Label = "x" } }, new());

        var act = async () => await mgr.CreateLoopTemplateAsync("bad", "", bad);
        await act.Should().ThrowAsync<InvalidOperationException>();
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
        versions.Select(v => v.VersionNumber).Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Fact]
    public async Task Clone_creates_a_separate_template_with_version_1()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        var srcId = await mgr.CreateLoopTemplateAsync("src", "", MinimalGraph());
        await mgr.UpdateLoopTemplateAsync(srcId, "src", "", MinimalGraph());

        var cloneId = await mgr.CloneLoopTemplateAsync(srcId, "src-copy");

        cloneId.Should().NotBe(srcId);
        var clone = await mgr.GetLoopTemplateAsync(cloneId);
        clone!.Name.Should().Be("src-copy");
        var versions = await mgr.GetVersionsAsync(cloneId);
        versions.Should().HaveCount(1);
        versions.Single().VersionNumber.Should().Be(1);
    }
}
