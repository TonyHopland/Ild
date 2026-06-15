using ILD.Core.Services.Implementations;
using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

public class RunAnalyticsServiceTests
{
    [Fact]
    public async Task Aggregates_per_template_success_rate_timing_routing_and_tokens()
    {
        using var db = new TestDb();
        var template = AddTemplate(db, "coder");
        var version = AddVersion(db, template.Id);

        // Two AI nodes wired with an on_failure edge and a reject custom edge so
        // we can attribute routings via LoopRunNode.IncomingEdgeId.
        var aiNode = AddNode(db, version.Id, NodeType.AI);
        var failNode = AddNode(db, version.Id, NodeType.Cmd);
        var onFailureEdge = AddEdge(db, aiNode.Id, failNode.Id, EdgeType.OnFailure, null);
        var rejectEdge = AddEdge(db, aiNode.Id, failNode.Id, EdgeType.Custom, "Reject");

        var t0 = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        // Run 1: completed, one AI node with usage (2s), one node entered via the
        // on_failure edge. A human-feedback request answered after 60s.
        var run1 = AddRun(db, version.Id, LoopRunStatus.Completed, t0);
        AddRunNode(db, run1.Id, aiNode.Id, t0, t0.AddSeconds(2), incomingEdgeId: null, input: 100, output: 40, cost: 0.10m);
        AddRunNode(db, run1.Id, failNode.Id, t0.AddSeconds(2), t0.AddSeconds(4), incomingEdgeId: onFailureEdge.Id);
        AddFeedback(db, run1.Id, seq: 1, EventType.HumanFeedbackRequested, t0.AddSeconds(10));
        AddFeedback(db, run1.Id, seq: 2, EventType.HumanFeedbackReceived, t0.AddSeconds(70));

        // Run 2: failed, one AI node with usage (4s), one node entered via the
        // reject edge.
        var run2 = AddRun(db, version.Id, LoopRunStatus.Failed, t0.AddMinutes(5));
        AddRunNode(db, run2.Id, aiNode.Id, t0, t0.AddSeconds(4), incomingEdgeId: null, input: 200, output: 60, cost: 0.20m);
        AddRunNode(db, run2.Id, failNode.Id, t0, t0.AddSeconds(1), incomingEdgeId: rejectEdge.Id);

        db.Context.SaveChanges();

        var overview = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync();

        var t = Assert.Single(overview.Templates);
        Assert.Equal("coder", t.TemplateName);
        Assert.Equal(2, t.TotalRuns);
        Assert.Equal(1, t.CompletedRuns);
        Assert.Equal(1, t.FailedRuns);
        Assert.Equal(0.5, t.SuccessRate); // 1 completed of 2 terminal
        // Node durations across the template: 2, 2, 4, 1 → mean 2.25s.
        Assert.NotNull(t.AvgNodeSeconds);
        Assert.Equal(2.25, t.AvgNodeSeconds!.Value, 3);
        Assert.Equal(1, t.OnFailureRoutings);
        Assert.Equal(1, t.RejectRoutings);
        Assert.Equal(60d, t.AvgHumanFeedbackSeconds);
        Assert.Equal(300, t.TotalInputTokens);
        Assert.Equal(100, t.TotalOutputTokens);
        Assert.Equal(0.30m, t.TotalCostUsd);

        Assert.Equal(2, overview.TotalRuns);
        Assert.Equal(300, overview.TotalInputTokens);
        Assert.Equal(0.30m, overview.TotalCostUsd);
    }

    [Fact]
    public async Task Template_with_no_terminal_runs_reports_zero_success_and_null_timing()
    {
        using var db = new TestDb();
        var template = AddTemplate(db, "planning");
        var version = AddVersion(db, template.Id);
        // A single still-running run with no finished nodes.
        AddRun(db, version.Id, LoopRunStatus.Running, new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc));
        db.Context.SaveChanges();

        var overview = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync();

        var t = Assert.Single(overview.Templates);
        Assert.Equal(1, t.TotalRuns);
        Assert.Equal(0, t.SuccessRate);
        Assert.Null(t.AvgNodeSeconds);
        Assert.Null(t.AvgHumanFeedbackSeconds);
        Assert.Equal(0, t.TotalInputTokens);
        Assert.Equal(0m, t.TotalCostUsd);
    }

    private static LoopTemplate AddTemplate(TestDb db, string name)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = name, RecoveryPolicy = RecoveryPolicy.AutoResume };
        db.Context.LoopTemplates.Add(template);
        return template;
    }

    private static LoopTemplateVersion AddVersion(TestDb db, Guid templateId)
    {
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = templateId, VersionNumber = 1 };
        db.Context.LoopTemplateVersions.Add(version);
        return version;
    }

    private static LoopNode AddNode(TestDb db, Guid versionId, NodeType type)
    {
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = versionId, NodeType = type, Label = type.ToString() };
        db.Context.LoopNodes.Add(node);
        return node;
    }

    private static LoopNodeEdge AddEdge(TestDb db, Guid source, Guid target, EdgeType type, string? name)
    {
        var edge = new LoopNodeEdge { Id = Guid.NewGuid(), SourceNodeId = source, TargetNodeId = target, EdgeType = type, Name = name };
        db.Context.LoopNodeEdges.Add(edge);
        return edge;
    }

    private static LoopRun AddRun(TestDb db, Guid versionId, LoopRunStatus status, DateTime startedAt)
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = $"WI-{Guid.NewGuid():N}",
            LoopTemplateVersionId = versionId,
            Status = status,
            StartedAt = startedAt,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        };
        db.Context.LoopRuns.Add(run);
        return run;
    }

    private static void AddRunNode(
        TestDb db, Guid runId, Guid nodeId, DateTime started, DateTime completed,
        Guid? incomingEdgeId, long? input = null, long? output = null, decimal? cost = null)
    {
        db.Context.LoopRunNodes.Add(new LoopRunNode
        {
            Id = Guid.NewGuid(),
            LoopRunId = runId,
            LoopNodeId = nodeId,
            Status = LoopRunNodeStatus.Succeeded,
            StartedAt = started,
            CompletedAt = completed,
            IncomingEdgeId = incomingEdgeId,
            InputTokens = input,
            OutputTokens = output,
            CostUsd = cost,
        });
    }

    private static void AddFeedback(TestDb db, Guid runId, int seq, EventType type, DateTime timestamp)
    {
        db.Context.EventLogs.Add(new EventLog
        {
            Id = Guid.NewGuid(),
            LoopRunId = runId,
            Sequence = seq,
            EventType = type,
            Timestamp = timestamp,
        });
    }
}
