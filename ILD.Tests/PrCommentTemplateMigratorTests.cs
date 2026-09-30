using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Migrations;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

public class PrCommentTemplateMigratorTests
{
    private static LoopNode Seed(TestDb db, NodeType type, string? config)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "Loop", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = type, Label = "n", Config = config };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopNodes.Add(node);
        db.Context.SaveChanges();
        return node;
    }

    private static async Task<string?> ConfigOf(TestDb db, Guid id)
        => (await db.Fresh().LoopNodes.SingleAsync(n => n.Id == id, TestContext.Current.CancellationToken)).Config;

    [Fact]
    public async Task Drops_the_template_and_keeps_every_other_field()
    {
        using var db = new TestDb();
        var pr = Seed(db, NodeType.PR,
            "{\"prDescriptionTemplate\":\"t\",\"prCommentTemplate\":\"Update\",\"outputs\":[{\"name\":\"on_merged\",\"reserved\":true}]}");

        Assert.Equal(1, await PrCommentTemplateMigrator.MigrateAsync(db.Context, TestContext.Current.CancellationToken));

        Assert.Equal(
            "{\"prDescriptionTemplate\":\"t\",\"outputs\":[{\"name\":\"on_merged\",\"reserved\":true}]}",
            await ConfigOf(db, pr.Id));
    }

    [Fact]
    public async Task Leaves_nodes_without_it_and_unreadable_configs_alone_and_a_second_run_does_nothing()
    {
        using var db = new TestDb();
        var plain = Seed(db, NodeType.PR, "{\"prDescriptionTemplate\":\"t\"}");
        var mentioned = Seed(db, NodeType.AI, "{\"prompt\":\"Say \\\"prCommentTemplate\\\" here\"}");
        var broken = Seed(db, NodeType.PR, "{\"prCommentTemplate\":");
        Seed(db, NodeType.PR, "{\"prCommentTemplate\":\"x\"}");

        Assert.Equal(1, await PrCommentTemplateMigrator.MigrateAsync(db.Context, TestContext.Current.CancellationToken));
        Assert.Equal(0, await PrCommentTemplateMigrator.MigrateAsync(db.Fresh(), TestContext.Current.CancellationToken));

        Assert.Equal("{\"prDescriptionTemplate\":\"t\"}", await ConfigOf(db, plain.Id));
        Assert.Equal("{\"prompt\":\"Say \\\"prCommentTemplate\\\" here\"}", await ConfigOf(db, mentioned.Id));
        Assert.Equal("{\"prCommentTemplate\":", await ConfigOf(db, broken.Id));
    }
}
