using System.Text.Json;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Migrations;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

public class ConditionSwitchMigratorTests
{
    /// <summary>
    /// Seeds a single-version template with one Condition node carrying the
    /// given raw config, wired to "true"/"false" custom edges plus the
    /// surrounding Start/Cleanup sinks. Returns the Condition node id.
    /// </summary>
    private static Guid SeedCondition(TestDb db, string config)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "Review Loop", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };

        var cond = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.Condition, Label = "gate", Config = config };
        var cleanup = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.Cleanup, Label = "done", Config = "{}" };

        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopNodes.AddRange(cond, cleanup);
        db.Context.LoopNodeEdges.AddRange(
            new LoopNodeEdge { Id = Guid.NewGuid(), SourceNodeId = cond.Id, TargetNodeId = cleanup.Id, EdgeType = EdgeType.Custom, Name = "true" },
            new LoopNodeEdge { Id = Guid.NewGuid(), SourceNodeId = cond.Id, TargetNodeId = cleanup.Id, EdgeType = EdgeType.Custom, Name = "false" });
        db.Context.SaveChanges();
        return cond.Id;
    }

    [Fact]
    public async Task Legacy_true_false_condition_is_rewritten_to_a_single_case_switch()
    {
        using var db = new TestDb();
        var condId = SeedCondition(db, "{\"variant\":\"TextMatches\",\"pattern\":\"approve\",\"output\":\"{{Node.Input}}\"}");

        var migrated = await ConditionSwitchMigrator.MigrateAsync(db.Context);

        Assert.Equal(1, migrated);

        var fresh = db.Fresh();
        var node = await fresh.LoopNodes.SingleAsync(n => n.Id == condId);
        using var doc = JsonDocument.Parse(node.Config!);
        var root = doc.RootElement;

        // The legacy top-level predicate keys are gone; the switch shape replaces them.
        Assert.False(root.TryGetProperty("variant", out _));
        Assert.False(root.TryGetProperty("pattern", out _));
        Assert.Equal("false", root.GetProperty("defaultEdge").GetString());
        // The pass-through output is preserved untouched.
        Assert.Equal("{{Node.Input}}", root.GetProperty("output").GetString());

        var c = Assert.Single(root.GetProperty("cases").EnumerateArray());
        Assert.Equal("TextMatches", c.GetProperty("variant").GetString());
        Assert.Equal("approve", c.GetProperty("pattern").GetString());
        Assert.Equal("true", c.GetProperty("edgeName").GetString());
    }

    [Fact]
    public async Task Migration_is_idempotent()
    {
        using var db = new TestDb();
        SeedCondition(db, "{\"variant\":\"PrExists\"}");

        Assert.Equal(1, await ConditionSwitchMigrator.MigrateAsync(db.Context));
        // Second run sees the switch shape already in place and changes nothing.
        Assert.Equal(0, await ConditionSwitchMigrator.MigrateAsync(db.Context));
    }

    [Fact]
    public async Task Legacy_condition_whose_text_contains_cases_still_migrates()
    {
        // Regression: the query must not exclude legacy rows just because their
        // free text (pattern/subject/output) contains the substring "cases" —
        // migration is the sole bridge, so a skipped row would dead-end at runtime.
        using var db = new TestDb();
        var condId = SeedCondition(db, "{\"variant\":\"TextMatches\",\"pattern\":\"edge cases\"}");

        var migrated = await ConditionSwitchMigrator.MigrateAsync(db.Context);

        Assert.Equal(1, migrated);
        var fresh = db.Fresh();
        var node = await fresh.LoopNodes.SingleAsync(n => n.Id == condId);
        using var doc = JsonDocument.Parse(node.Config!);
        var c = Assert.Single(doc.RootElement.GetProperty("cases").EnumerateArray());
        Assert.Equal("edge cases", c.GetProperty("pattern").GetString());
        Assert.Equal("true", c.GetProperty("edgeName").GetString());
    }

    [Fact]
    public async Task An_already_switch_condition_is_left_untouched()
    {
        using var db = new TestDb();
        var switchConfig = "{\"cases\":[{\"variant\":\"HasTag\",\"tag\":\"urgent\",\"edgeName\":\"urgent\"}],\"defaultEdge\":\"otherwise\"}";
        var condId = SeedCondition(db, switchConfig);

        var migrated = await ConditionSwitchMigrator.MigrateAsync(db.Context);

        Assert.Equal(0, migrated);
        var fresh = db.Fresh();
        var node = await fresh.LoopNodes.SingleAsync(n => n.Id == condId);
        Assert.Equal(switchConfig, node.Config);
    }
}
