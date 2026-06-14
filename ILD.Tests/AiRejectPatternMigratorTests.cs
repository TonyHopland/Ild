using System.Text.Json;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Migrations;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

public class AiRejectPatternMigratorTests
{
    /// <summary>
    /// Builds a single-version template: an AI node carrying a legacy
    /// rejectPattern, wired OnSuccess -> pr and OnFailure -> retry, with no
    /// custom edge. Returns the AI / pr / retry node ids.
    /// </summary>
    private static (Guid ai, Guid pr, Guid retry) SeedLegacyAiNode(TestDb db, string rejectPattern)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "Code Review Loop", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };

        var ai = new LoopNode
        {
            Id = Guid.NewGuid(),
            LoopTemplateVersionId = version.Id,
            NodeType = NodeType.AI,
            Label = "ai-review",
            Config = $"{{\"prompt\":\"review\",\"rejectPattern\":\"{rejectPattern}\"}}",
        };
        var pr = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.PR, Label = "pr", Config = "{}" };
        var retry = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.Prompt, Label = "retry", Config = "{}" };

        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopNodes.AddRange(ai, pr, retry);
        db.Context.LoopNodeEdges.AddRange(
            new LoopNodeEdge { Id = Guid.NewGuid(), SourceNodeId = ai.Id, TargetNodeId = pr.Id, EdgeType = EdgeType.OnSuccess },
            new LoopNodeEdge { Id = Guid.NewGuid(), SourceNodeId = ai.Id, TargetNodeId = retry.Id, EdgeType = EdgeType.OnFailure });
        db.Context.SaveChanges();
        return (ai.Id, pr.Id, retry.Id);
    }

    [Fact]
    public async Task Legacy_rejectPattern_routes_a_matching_output_to_the_old_failure_target_after_migration()
    {
        using var db = new TestDb();
        var (aiId, _, retryId) = SeedLegacyAiNode(db, "Reject");

        var migrated = await AiRejectPatternMigrator.MigrateAsync(db.Context);

        Assert.Equal(1, migrated);

        var fresh = db.Fresh();

        // A Custom "Reject" edge now carries the old reject routing to the same
        // node the OnFailure edge targeted (the retry node).
        var rejectEdge = await fresh.LoopNodeEdges
            .SingleAsync(e => e.SourceNodeId == aiId && e.EdgeType == EdgeType.Custom && e.Name == "Reject");
        Assert.Equal(retryId, rejectEdge.TargetNodeId);

        // The config drops rejectPattern and gains a matching match rule whose
        // edge name resolves to that Custom edge.
        var node = await fresh.LoopNodes.SingleAsync(n => n.Id == aiId);
        using var doc = JsonDocument.Parse(node.Config!);
        Assert.False(doc.RootElement.TryGetProperty("rejectPattern", out _));
        var rule = Assert.Single(doc.RootElement.GetProperty("matchRules").EnumerateArray());
        Assert.Equal("Reject", rule.GetProperty("pattern").GetString());
        Assert.Equal("Reject", rule.GetProperty("edgeName").GetString());
    }

    [Fact]
    public async Task Migration_is_idempotent_and_does_not_duplicate_edges_or_rules()
    {
        using var db = new TestDb();
        var (aiId, _, _) = SeedLegacyAiNode(db, "Reject");

        Assert.Equal(1, await AiRejectPatternMigrator.MigrateAsync(db.Context));
        // Second run sees no rejectPattern left and changes nothing.
        Assert.Equal(0, await AiRejectPatternMigrator.MigrateAsync(db.Context));

        var fresh = db.Fresh();
        var rejectEdges = await fresh.LoopNodeEdges
            .CountAsync(e => e.SourceNodeId == aiId && e.EdgeType == EdgeType.Custom && e.Name == "Reject");
        Assert.Equal(1, rejectEdges);
    }
}
