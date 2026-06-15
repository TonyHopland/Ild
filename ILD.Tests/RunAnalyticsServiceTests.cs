using ILD.Core.Services.Implementations;
using ILD.Data.DTOs;
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
        AddRunNode(db, run1.Id, aiNode.Id, t0, t0.AddSeconds(2), incomingEdgeId: null, input: 100, output: 40, cost: 0.10m, aiProvider: "claude");
        AddRunNode(db, run1.Id, failNode.Id, t0.AddSeconds(2), t0.AddSeconds(4), incomingEdgeId: onFailureEdge.Id);
        AddFeedback(db, run1.Id, seq: 1, EventType.HumanFeedbackRequested, t0.AddSeconds(10));
        AddFeedback(db, run1.Id, seq: 2, EventType.HumanFeedbackReceived, t0.AddSeconds(70));

        // Run 2: failed, one AI node with usage (4s), one node entered via the
        // reject edge.
        var run2 = AddRun(db, version.Id, LoopRunStatus.Failed, t0.AddMinutes(5));
        AddRunNode(db, run2.Id, aiNode.Id, t0, t0.AddSeconds(4), incomingEdgeId: null, input: 200, output: 60, cost: 0.20m, aiProvider: "claude");
        AddRunNode(db, run2.Id, failNode.Id, t0, t0.AddSeconds(1), incomingEdgeId: rejectEdge.Id);

        db.Context.SaveChanges();

        var overview = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync(new AnalyticsQuery());

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

        // Both runs are attributed to the "claude" provider.
        var provider = Assert.Single(overview.Providers);
        Assert.Equal("claude", provider.Provider);
        Assert.Equal(2, provider.TotalRuns);
        Assert.Equal(0.30m, provider.TotalCostUsd);
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

        var overview = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync(new AnalyticsQuery());

        var t = Assert.Single(overview.Templates);
        Assert.Equal(1, t.TotalRuns);
        Assert.Equal(0, t.SuccessRate);
        Assert.Null(t.AvgNodeSeconds);
        Assert.Null(t.AvgHumanFeedbackSeconds);
        Assert.Equal(0, t.TotalInputTokens);
        Assert.Equal(0m, t.TotalCostUsd);

        // A run with no AI node is attributed to the "(none)" provider bucket.
        var provider = Assert.Single(overview.Providers);
        Assert.Equal(ILD.Data.Analytics.RunAnalyticsAggregator.NoProvider, provider.Provider);
    }

    [Fact]
    public async Task Cost_survives_run_deletion_via_the_durable_rollup()
    {
        using var db = new TestDb();
        var template = AddTemplate(db, "coder");
        var version = AddVersion(db, template.Id);
        var aiNode = AddNode(db, version.Id, NodeType.AI);
        var t0 = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

        var run = AddRun(db, version.Id, LoopRunStatus.Completed, t0);
        AddRunNode(db, run.Id, aiNode.Id, t0, t0.AddSeconds(3), incomingEdgeId: null, input: 500, output: 120, cost: 0.42m, aiProvider: "claude");
        db.Context.SaveChanges();

        // Reclaim/delete the run — its LoopRunNode rows are cascade-deleted.
        Assert.True(await db.LoopRuns.DeleteAsync(run.Id));
        Assert.Empty(db.Fresh().LoopRunNodes.ToList());

        var overview = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync(new AnalyticsQuery());

        // The figures persist from the archived bucket even though the run is gone.
        Assert.Equal(1, overview.TotalRuns);
        Assert.Equal(500, overview.TotalInputTokens);
        Assert.Equal(0.42m, overview.TotalCostUsd);
        var t = Assert.Single(overview.Templates);
        Assert.Equal("coder", t.TemplateName);
        Assert.Equal(1, t.CompletedRuns);
        var provider = Assert.Single(overview.Providers);
        Assert.Equal("claude", provider.Provider);
        Assert.Equal(0.42m, provider.TotalCostUsd);
    }

    [Fact]
    public async Task Merges_live_and_archived_runs_without_double_counting()
    {
        using var db = new TestDb();
        var template = AddTemplate(db, "coder");
        var version = AddVersion(db, template.Id);
        var aiNode = AddNode(db, version.Id, NodeType.AI);
        var t0 = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

        // One run we delete (archived), one we keep (live), same day & provider.
        var deleted = AddRun(db, version.Id, LoopRunStatus.Completed, t0);
        AddRunNode(db, deleted.Id, aiNode.Id, t0, t0.AddSeconds(1), incomingEdgeId: null, input: 100, output: 10, cost: 0.10m, aiProvider: "claude");
        var live = AddRun(db, version.Id, LoopRunStatus.Completed, t0.AddHours(1));
        AddRunNode(db, live.Id, aiNode.Id, t0, t0.AddSeconds(1), incomingEdgeId: null, input: 200, output: 20, cost: 0.20m, aiProvider: "claude");
        db.Context.SaveChanges();

        await db.LoopRuns.DeleteAsync(deleted.Id);

        var overview = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync(new AnalyticsQuery());

        Assert.Equal(2, overview.TotalRuns); // archived + live, counted once each
        Assert.Equal(300, overview.TotalInputTokens);
        Assert.Equal(0.30m, overview.TotalCostUsd);
    }

    [Fact]
    public async Task Filters_by_provider_and_date_range_and_buckets_the_series()
    {
        using var db = new TestDb();
        var template = AddTemplate(db, "coder");
        var version = AddVersion(db, template.Id);
        var aiNode = AddNode(db, version.Id, NodeType.AI);

        var june1 = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        var june8 = new DateTime(2026, 6, 8, 9, 0, 0, DateTimeKind.Utc);

        var rClaude = AddRun(db, version.Id, LoopRunStatus.Completed, june1);
        AddRunNode(db, rClaude.Id, aiNode.Id, june1, june1.AddSeconds(1), incomingEdgeId: null, input: 100, output: 10, cost: 0.10m, aiProvider: "claude");
        var rOpen = AddRun(db, version.Id, LoopRunStatus.Completed, june8);
        AddRunNode(db, rOpen.Id, aiNode.Id, june8, june8.AddSeconds(1), incomingEdgeId: null, input: 200, output: 20, cost: 0.20m, aiProvider: "opencode");
        db.Context.SaveChanges();

        var service = new RunAnalyticsService(db.Fresh());

        // Provider filter keeps only the claude run.
        var claudeOnly = await service.GetOverviewAsync(new AnalyticsQuery(Provider: "claude"));
        Assert.Equal(1, claudeOnly.TotalRuns);
        Assert.Equal(0.10m, claudeOnly.TotalCostUsd);
        // The unfiltered provider list still offers both for the dropdown.
        Assert.Contains("claude", claudeOnly.AvailableProviders);
        Assert.Contains("opencode", claudeOnly.AvailableProviders);

        // Date range keeps only the June 8 run.
        var service2 = new RunAnalyticsService(db.Fresh());
        var week2 = await service2.GetOverviewAsync(new AnalyticsQuery(
            From: new DateOnly(2026, 6, 5),
            To: new DateOnly(2026, 6, 10)));
        Assert.Equal(1, week2.TotalRuns);
        Assert.Equal(0.20m, week2.TotalCostUsd);

        // Weekly granularity buckets the two runs into two ISO weeks.
        var service3 = new RunAnalyticsService(db.Fresh());
        var weekly = await service3.GetOverviewAsync(new AnalyticsQuery(Granularity: AnalyticsGranularity.Week));
        Assert.Equal(AnalyticsGranularity.Week, weekly.Granularity);
        Assert.Equal(2, weekly.Series.Count);
        Assert.All(weekly.Series, p => Assert.Equal(DayOfWeek.Monday, p.PeriodStart.DayOfWeek));

        // Monthly granularity collapses them into one June bucket.
        var service4 = new RunAnalyticsService(db.Fresh());
        var monthly = await service4.GetOverviewAsync(new AnalyticsQuery(Granularity: AnalyticsGranularity.Month));
        var point = Assert.Single(monthly.Series);
        Assert.Equal(new DateOnly(2026, 6, 1), point.PeriodStart);
        Assert.Equal(2, point.Runs);
        Assert.Equal(0.30m, point.CostUsd);
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
        Guid? incomingEdgeId, long? input = null, long? output = null, decimal? cost = null, string? aiProvider = null)
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
            AiProvider = aiProvider,
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
