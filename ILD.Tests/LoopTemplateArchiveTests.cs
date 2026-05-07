using FluentAssertions;
using ILD.Data.DTOs;
using ILD.Core.Services.Implementations;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

public class LoopTemplateArchiveTests
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
    public async Task GetAll_excludes_archived_templates_by_default()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        var live = await mgr.CreateLoopTemplateAsync("live", "", MinimalGraph());
        var archived = await mgr.CreateLoopTemplateAsync("archived", "", MinimalGraph());

        await mgr.ArchiveLoopTemplateAsync(archived);

        var all = await mgr.GetAllLoopTemplatesAsync();
        all.Select(t => t.Id).Should().Contain(live);
        all.Select(t => t.Id).Should().NotContain(archived);
    }

    [Fact]
    public async Task GetAll_includes_archived_when_flag_is_true()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        var live = await mgr.CreateLoopTemplateAsync("live", "", MinimalGraph());
        var archived = await mgr.CreateLoopTemplateAsync("archived", "", MinimalGraph());

        await mgr.ArchiveLoopTemplateAsync(archived);

        var all = await mgr.GetAllLoopTemplatesAsync(includeArchived: true);
        all.Select(t => t.Id).Should().Contain(live);
        all.Select(t => t.Id).Should().Contain(archived);
    }

    [Fact]
    public async Task Unarchive_restores_template_to_live_list()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        var id = await mgr.CreateLoopTemplateAsync("t", "", MinimalGraph());
        await mgr.ArchiveLoopTemplateAsync(id);

        (await mgr.GetAllLoopTemplatesAsync()).Should().BeEmpty();

        await mgr.UnarchiveLoopTemplateAsync(id);

        (await mgr.GetAllLoopTemplatesAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Archive_sets_IsArchived_true()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        var id = await mgr.CreateLoopTemplateAsync("t", "", MinimalGraph());
        await mgr.ArchiveLoopTemplateAsync(id);

        var template = await db.Context.LoopTemplates.FindAsync(id);
        template.Should().NotBeNull();
        template!.IsArchived.Should().BeTrue();
    }

    [Fact]
    public async Task Unarchive_sets_IsArchived_false()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        var id = await mgr.CreateLoopTemplateAsync("t", "", MinimalGraph());
        await mgr.ArchiveLoopTemplateAsync(id);
        await mgr.UnarchiveLoopTemplateAsync(id);

        var template = await db.Context.LoopTemplates.FindAsync(id);
        template.Should().NotBeNull();
        template!.IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task Archive_nonexistent_template_is_noop()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        var act = () => mgr.ArchiveLoopTemplateAsync(Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Unarchive_nonexistent_template_is_noop()
    {
        using var db = new TestDb();
        var mgr = new LoopTemplateManager(db.LoopTemplates);

        var act = () => mgr.UnarchiveLoopTemplateAsync(Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }
}
