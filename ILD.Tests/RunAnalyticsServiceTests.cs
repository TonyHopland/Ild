using ILD.Core.Services.Implementations;
using ILD.Data.Analytics;
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
        AddFeedback(db, run1.Id, id: 1, EventType.HumanFeedbackRequested, t0.AddSeconds(10));
        AddFeedback(db, run1.Id, id: 2, EventType.HumanFeedbackReceived, t0.AddSeconds(70));

        // Run 2: failed, one AI node with usage (4s), one node entered via the
        // reject edge.
        var run2 = AddRun(db, version.Id, LoopRunStatus.Failed, t0.AddMinutes(5));
        AddRunNode(db, run2.Id, aiNode.Id, t0, t0.AddSeconds(4), incomingEdgeId: null, input: 200, output: 60, cost: 0.20m, aiProvider: "claude");
        AddRunNode(db, run2.Id, failNode.Id, t0, t0.AddSeconds(1), incomingEdgeId: rejectEdge.Id);

        db.Context.SaveChanges();

        var overview = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync(new AnalyticsQuery(), TestContext.Current.CancellationToken);

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

        var overview = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync(new AnalyticsQuery(), TestContext.Current.CancellationToken);

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

        var overview = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync(new AnalyticsQuery(), TestContext.Current.CancellationToken);

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

        var overview = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync(new AnalyticsQuery(), TestContext.Current.CancellationToken);

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
        var claudeOnly = await service.GetOverviewAsync(new AnalyticsQuery(Provider: "claude"), TestContext.Current.CancellationToken);
        Assert.Equal(1, claudeOnly.TotalRuns);
        Assert.Equal(0.10m, claudeOnly.TotalCostUsd);
        // The unfiltered provider list still offers both for the dropdown.
        Assert.Contains("claude", claudeOnly.AvailableProviders);
        Assert.Contains("opencode", claudeOnly.AvailableProviders);

        // Date range keeps only the June 8 run.
        var service2 = new RunAnalyticsService(db.Fresh());
        var week2 = await service2.GetOverviewAsync(new AnalyticsQuery(
            From: new DateOnly(2026, 6, 5),
            To: new DateOnly(2026, 6, 10)), TestContext.Current.CancellationToken);
        Assert.Equal(1, week2.TotalRuns);
        Assert.Equal(0.20m, week2.TotalCostUsd);

        // Weekly granularity buckets the two runs into two ISO weeks.
        var service3 = new RunAnalyticsService(db.Fresh());
        var weekly = await service3.GetOverviewAsync(new AnalyticsQuery(Granularity: AnalyticsGranularity.Week), TestContext.Current.CancellationToken);
        Assert.Equal(AnalyticsGranularity.Week, weekly.Granularity);
        Assert.Equal(2, weekly.Series.Count);
        Assert.All(weekly.Series, p => Assert.Equal(DayOfWeek.Monday, p.PeriodStart.DayOfWeek));

        // Monthly granularity collapses them into one June bucket.
        var service4 = new RunAnalyticsService(db.Fresh());
        var monthly = await service4.GetOverviewAsync(new AnalyticsQuery(Granularity: AnalyticsGranularity.Month), TestContext.Current.CancellationToken);
        var point = Assert.Single(monthly.Series);
        Assert.Equal(new DateOnly(2026, 6, 1), point.PeriodStart);
        Assert.Equal(2, point.Runs);
        Assert.Equal(0.30m, point.CostUsd);
    }

    [Fact]
    public async Task Chat_usage_adds_tokens_and_cost_to_totals_providers_and_series_but_never_to_runs_or_templates()
    {
        using var db = new TestDb();
        var template = AddTemplate(db, "coder");
        var version = AddVersion(db, template.Id);
        var aiNode = AddNode(db, version.Id, NodeType.AI);
        var june1 = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        var june8 = new DateTime(2026, 6, 8, 23, 30, 0, DateTimeKind.Utc);

        var run = AddRun(db, version.Id, LoopRunStatus.Completed, june1);
        AddRunNode(db, run.Id, aiNode.Id, june1, june1.AddSeconds(1), incomingEdgeId: null, input: 100, output: 10, cost: 0.10m, aiProvider: "claude");

        var chat = AddChat(db);
        AddChatUsage(db, chat.Id, ChatUsageKind.Turn, "claude", 50, 5, 0.05m, june1.AddHours(2));
        // The title ran on a provider no loop run has used.
        AddChatUsage(db, chat.Id, ChatUsageKind.Title, "haiku-fast", 7, 3, 0.01m, june1.AddHours(2));
        AddChatUsage(db, chat.Id, ChatUsageKind.Turn, "claude", 20, 2, null, june8);
        db.Context.SaveChanges();

        var overview = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync(new AnalyticsQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(1, overview.TotalRuns);
        Assert.Equal(177, overview.TotalInputTokens);
        Assert.Equal(20, overview.TotalOutputTokens);
        Assert.Equal(0.16m, overview.TotalCostUsd);

        var t = Assert.Single(overview.Templates);
        Assert.Equal(1, t.TotalRuns);
        Assert.Equal(100, t.TotalInputTokens);
        Assert.Equal(10, t.TotalOutputTokens);
        Assert.Equal(0.10m, t.TotalCostUsd);

        Assert.Equal(2, overview.Providers.Count);
        var claude = Assert.Single(overview.Providers, p => p.Provider == "claude");
        Assert.Equal(1, claude.TotalRuns);
        Assert.Equal(170, claude.TotalInputTokens);
        Assert.Equal(17, claude.TotalOutputTokens);
        Assert.Equal(0.15m, claude.TotalCostUsd);
        var fast = Assert.Single(overview.Providers, p => p.Provider == "haiku-fast");
        Assert.Equal(0, fast.TotalRuns);
        Assert.Equal(7, fast.TotalInputTokens);
        Assert.Equal(3, fast.TotalOutputTokens);
        Assert.Equal(0.01m, fast.TotalCostUsd);
        Assert.Contains("haiku-fast", overview.AvailableProviders);

        // Titles are counted under the chat but are not turns.
        var row = Assert.Single(overview.Chats);
        Assert.Null(row.ScheduleId);
        Assert.Null(row.ScheduleName);
        Assert.Equal(2, row.TotalTurns);
        Assert.Equal(77, row.TotalInputTokens);
        Assert.Equal(10, row.TotalOutputTokens);
        Assert.Equal(0.06m, row.TotalCostUsd);

        var expectedSeries = new Dictionary<AnalyticsGranularity, AnalyticsSeriesPoint[]>
        {
            [AnalyticsGranularity.Day] =
            [
                new(new DateOnly(2026, 6, 1), 1, 157, 18, 0.16m),
                new(new DateOnly(2026, 6, 8), 0, 20, 2, 0m),
            ],
            // 2026-06-01 and 2026-06-08 are both Mondays.
            [AnalyticsGranularity.Week] =
            [
                new(new DateOnly(2026, 6, 1), 1, 157, 18, 0.16m),
                new(new DateOnly(2026, 6, 8), 0, 20, 2, 0m),
            ],
            [AnalyticsGranularity.Month] = [new(new DateOnly(2026, 6, 1), 1, 177, 20, 0.16m)],
            [AnalyticsGranularity.Year] = [new(new DateOnly(2026, 1, 1), 1, 177, 20, 0.16m)],
        };
        foreach (var (granularity, expected) in expectedSeries)
        {
            var byPeriod = await new RunAnalyticsService(db.Fresh())
                .GetOverviewAsync(new AnalyticsQuery(Granularity: granularity), TestContext.Current.CancellationToken);
            Assert.Equal(expected, byPeriod.Series);
        }
    }

    [Fact]
    public async Task Chat_rows_split_by_schedule_and_take_the_schedules_current_name_else_the_last_recorded_one()
    {
        using var db = new TestDb();
        var day = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

        // A live schedule renamed since its usage was recorded.
        var live = AddSchedule(db, "Nightly retro");
        var liveChat = AddChat(db, live.Id);
        AddChatUsage(db, liveChat.Id, ChatUsageKind.Turn, "claude", 10, 1, 0.01m, day, live.Id, "Retro");
        AddChatUsage(db, liveChat.Id, ChatUsageKind.Title, "claude", 2, 1, 0.001m, day, live.Id, "Retro");

        // A deleted schedule: its chat was unlinked, the usage still names it.
        var goneId = Guid.NewGuid();
        var orphaned = AddChat(db);
        AddChatUsage(db, orphaned.Id, ChatUsageKind.Turn, "claude", 20, 2, 0.02m, day, goneId, "Digest");
        AddChatUsage(db, orphaned.Id, ChatUsageKind.Turn, "claude", 30, 3, 0.03m, day.AddHours(1), goneId, "Weekly digest");

        var plain = AddChat(db);
        AddChatUsage(db, plain.Id, ChatUsageKind.Turn, "claude", 40, 4, 0.04m, day);
        db.Context.SaveChanges();

        var overview = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync(new AnalyticsQuery(), TestContext.Current.CancellationToken);

        Assert.Empty(overview.Templates);
        Assert.Equal(0, overview.TotalRuns);
        Assert.Equal(3, overview.Chats.Count);
        Assert.Equal(new ChatAnalytics(live.Id, "Nightly retro", 1, 12, 2, 0.011m), Assert.Single(overview.Chats, c => c.ScheduleId == live.Id));
        Assert.Equal(new ChatAnalytics(goneId, "Weekly digest", 2, 50, 5, 0.05m), Assert.Single(overview.Chats, c => c.ScheduleId == goneId));
        Assert.Equal(new ChatAnalytics(null, null, 1, 40, 4, 0.04m), Assert.Single(overview.Chats, c => c.ScheduleId == null));
    }

    [Fact]
    public async Task Date_and_provider_filters_apply_to_chat_usage_and_drop_chat_rows_left_empty()
    {
        using var db = new TestDb();
        var june1 = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        var june8Late = new DateTime(2026, 6, 8, 23, 59, 0, DateTimeKind.Utc);
        var schedule = AddSchedule(db, "Nightly retro");

        var plain = AddChat(db);
        AddChatUsage(db, plain.Id, ChatUsageKind.Turn, "claude", 100, 10, 0.10m, june1);
        var scheduled = AddChat(db, schedule.Id);
        AddChatUsage(db, scheduled.Id, ChatUsageKind.Turn, "opencode", 200, 20, 0.20m, june8Late, schedule.Id, "Nightly retro");
        // A title of the plain chat on another provider, inside the date range.
        AddChatUsage(db, plain.Id, ChatUsageKind.Title, "opencode", 5, 1, 0.005m, june8Late);
        db.Context.SaveChanges();

        var claudeOnly = await new RunAnalyticsService(db.Fresh())
            .GetOverviewAsync(new AnalyticsQuery(Provider: "CLAUDE"), TestContext.Current.CancellationToken);
        Assert.Equal(100, claudeOnly.TotalInputTokens);
        Assert.Equal(0.10m, claudeOnly.TotalCostUsd);
        Assert.Equal("claude", Assert.Single(claudeOnly.Providers).Provider);
        Assert.Equal(new AnalyticsSeriesPoint(new DateOnly(2026, 6, 1), 0, 100, 10, 0.10m), Assert.Single(claudeOnly.Series));
        Assert.Equal(new ChatAnalytics(null, null, 1, 100, 10, 0.10m), Assert.Single(claudeOnly.Chats));
        Assert.Contains("opencode", claudeOnly.AvailableProviders);

        var week2 = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync(new AnalyticsQuery(
            From: new DateOnly(2026, 6, 5),
            To: new DateOnly(2026, 6, 8)), TestContext.Current.CancellationToken);
        Assert.Equal(205, week2.TotalInputTokens);
        Assert.Equal(0.205m, week2.TotalCostUsd);
        Assert.Equal(2, week2.Chats.Count);
        Assert.Equal(new ChatAnalytics(schedule.Id, "Nightly retro", 1, 200, 20, 0.20m), Assert.Single(week2.Chats, c => c.ScheduleId == schedule.Id));
        // Only the title is left of the plain chat: a row, but no turns.
        Assert.Equal(new ChatAnalytics(null, null, 0, 5, 1, 0.005m), Assert.Single(week2.Chats, c => c.ScheduleId == null));

        var claudeWeek2 = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync(new AnalyticsQuery(
            From: new DateOnly(2026, 6, 5),
            To: new DateOnly(2026, 6, 8),
            Provider: "claude"), TestContext.Current.CancellationToken);
        Assert.Equal(0, claudeWeek2.TotalInputTokens);
        Assert.Empty(claudeWeek2.Chats);
        Assert.Empty(claudeWeek2.Providers);
        Assert.Empty(claudeWeek2.Series);
    }

    [Theory]
    [InlineData("renamed")]
    [InlineData("renamed,original")]
    [InlineData("original,renamed")]
    public async Task A_deleted_schedules_row_keeps_its_newest_name_whichever_of_its_chats_were_deleted_first(string deletionOrder)
    {
        using var db = new TestDb();
        var day = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var schedule = AddSchedule(db, "New");
        var chats = new Dictionary<string, ChatSession>
        {
            ["original"] = AddChat(db, schedule.Id),
            ["renamed"] = AddChat(db, schedule.Id),
        };
        AddChatUsage(db, chats["original"].Id, ChatUsageKind.Turn, "claude", 10, 1, 0.01m, day.AddHours(9), schedule.Id, "Old");
        AddChatUsage(db, chats["renamed"].Id, ChatUsageKind.Turn, "claude", 20, 2, 0.02m, day.AddHours(10), schedule.Id, "New");
        db.Context.SaveChanges();

        foreach (var name in deletionOrder.Split(','))
        {
            using var ctx = db.Fresh();
            await ChatUsageLedger.ArchiveAsync(ctx, chats[name].Id, TestContext.Current.CancellationToken);
            ctx.ChatSessions.Remove(ctx.ChatSessions.Single(c => c.Id == chats[name].Id));
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using (var ctx = db.Fresh())
        {
            ctx.ChatSchedules.Remove(ctx.ChatSchedules.Single(c => c.Id == schedule.Id));
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var overview = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync(new AnalyticsQuery(), TestContext.Current.CancellationToken);
        Assert.Equal(new ChatAnalytics(schedule.Id, "New", 2, 30, 3, 0.03m), Assert.Single(overview.Chats));
    }

    private static ChatSchedule AddSchedule(TestDb db, string name)
    {
        var schedule = new ChatSchedule
        {
            Id = Guid.NewGuid(),
            UserId = "alice",
            Name = name,
            Prompt = "Check.",
            CronExpression = "0 3 * * *",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.ChatSchedules.Add(schedule);
        return schedule;
    }

    private static ChatSession AddChat(TestDb db, Guid? scheduleId = null)
    {
        var chat = new ChatSession
        {
            Id = Guid.NewGuid(),
            UserId = "alice",
            AiProviderId = Guid.NewGuid(),
            ProviderType = "claude-code",
            ScheduleId = scheduleId,
            CreatedAt = DateTime.UtcNow,
        };
        db.Context.ChatSessions.Add(chat);
        return chat;
    }

    private static void AddChatUsage(
        TestDb db, Guid chatId, ChatUsageKind kind, string provider, long input, long output, decimal? cost,
        DateTime createdAt, Guid? scheduleId = null, string? scheduleName = null)
    {
        db.Context.ChatUsages.Add(new ChatUsage
        {
            Id = Guid.NewGuid(),
            ChatSessionId = chatId,
            Kind = kind,
            AiProvider = provider,
            InputTokens = input,
            OutputTokens = output,
            CostUsd = cost,
            ScheduleId = scheduleId,
            ScheduleName = scheduleName,
            CreatedAt = createdAt,
        });
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

    private static void AddFeedback(TestDb db, Guid runId, long id, EventType type, DateTime timestamp)
    {
        db.Context.EventLogs.Add(new EventLog
        {
            Id = id,
            LoopRunId = runId,
            EventType = type,
            Timestamp = timestamp,
        });
    }
}
