using ILD.Core.Services.Implementations;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

public class AnalyticsCleanupServiceTests
{
    private static readonly DateTime Today = DateTime.UtcNow.Date;

    [Fact]
    public async Task Clearing_all_removes_the_three_analytics_sources_and_leaves_live_runs_and_chats()
    {
        using var db = new TestDb();
        var live = SeedLiveRun(db);
        var chat = SeedChat(db);
        SeedRunBucket(db, Today.AddDays(-1), "gone-run", 5);
        SeedChatBucket(db, Today.AddDays(-1), 7);
        SeedUsage(db, chat, Today.AddHours(-1), 11);
        db.Context.SaveChanges();

        await new AnalyticsCleanupService(db.Fresh()).ClearAsync(null, TestContext.Current.CancellationToken);

        await using var check = db.Fresh();
        Assert.Empty(check.LoopRunAnalyticsBuckets);
        Assert.Empty(check.ChatAnalyticsBuckets);
        Assert.Empty(check.ChatUsages);
        Assert.Single(check.LoopRuns, r => r.Id == live);
        Assert.Equal(2, check.LoopRunNodes.Count(n => n.LoopRunId == live));
        Assert.Single(check.ChatSessions, s => s.Id == chat);
        Assert.Single(check.ChatMessages, m => m.ChatSessionId == chat);

        var overview = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync(new AnalyticsQuery(), TestContext.Current.CancellationToken);
        Assert.Equal(1, overview.TotalRuns);
        Assert.Equal(100, overview.TotalInputTokens);
        Assert.Equal(0.10m, overview.TotalCostUsd);
        Assert.Empty(overview.Chats);
    }

    [Fact]
    public async Task Clearing_all_with_no_live_runs_reports_an_empty_overview()
    {
        using var db = new TestDb();
        SeedRunBucket(db, Today.AddDays(-3), "gone-run", 5);
        SeedChatBucket(db, Today.AddDays(-3), 7);
        SeedUsage(db, SeedChat(db), Today.AddDays(-1), 11);
        db.Context.SaveChanges();

        await new AnalyticsCleanupService(db.Fresh()).ClearAsync(null, TestContext.Current.CancellationToken);

        var overview = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync(new AnalyticsQuery(), TestContext.Current.CancellationToken);
        Assert.Equal(0, overview.TotalRuns);
        Assert.Equal(0, overview.TotalInputTokens);
        Assert.Equal(0m, overview.TotalCostUsd);
        Assert.Empty(overview.Templates);
        Assert.Empty(overview.Chats);
        Assert.Empty(overview.Providers);
        Assert.Empty(overview.Series);
    }

    [Fact]
    public async Task Clearing_older_than_days_removes_only_rows_before_the_utc_midnight_cutoff()
    {
        using var db = new TestDb();
        var live = SeedLiveRun(db);
        var chat = SeedChat(db);
        var cutoff = Today.AddDays(-5);
        SeedRunBucket(db, cutoff.AddDays(-1), "old-run", 1);
        SeedRunBucket(db, cutoff, "edge-run", 2);
        SeedRunBucket(db, cutoff.AddDays(2), "new-run", 3);
        SeedChatBucket(db, cutoff.AddDays(-1), 4);
        SeedChatBucket(db, cutoff, 5);
        SeedUsage(db, chat, cutoff.AddSeconds(-1), 6);
        SeedUsage(db, chat, cutoff, 7);
        SeedUsage(db, chat, Today, 8);
        db.Context.SaveChanges();

        await new AnalyticsCleanupService(db.Fresh()).ClearAsync(5, TestContext.Current.CancellationToken);

        await using var check = db.Fresh();
        Assert.Equal(new[] { "edge-run", "new-run" }, check.LoopRunAnalyticsBuckets.Select(b => b.TemplateName).OrderBy(n => n));
        Assert.Equal(new[] { 5L }, check.ChatAnalyticsBuckets.Select(b => b.TotalInputTokens));
        Assert.Equal(new[] { 7L, 8L }, check.ChatUsages.Select(u => u.InputTokens).OrderBy(n => n));
        Assert.Single(check.LoopRuns, r => r.Id == live);
        Assert.Single(check.ChatSessions, s => s.Id == chat);
        Assert.Single(check.ChatMessages, m => m.ChatSessionId == chat);
    }

    [Fact]
    public async Task Usage_recorded_after_a_clear_is_counted()
    {
        using var db = new TestDb();
        var chat = SeedChat(db);
        SeedUsage(db, chat, Today.AddHours(-2), 11);
        db.Context.SaveChanges();
        var service = new AnalyticsCleanupService(db.Fresh());
        await service.ClearAsync(null, TestContext.Current.CancellationToken);

        SeedUsage(db, chat, DateTime.UtcNow, 40);
        db.Context.SaveChanges();

        var overview = await new RunAnalyticsService(db.Fresh()).GetOverviewAsync(new AnalyticsQuery(), TestContext.Current.CancellationToken);
        Assert.Equal(40, overview.TotalInputTokens);
        Assert.Equal(40, Assert.Single(overview.Chats).TotalInputTokens);
    }

    [Fact]
    public async Task A_clear_that_is_cancelled_removes_nothing_from_any_source()
    {
        using var db = new TestDb();
        var chat = SeedChat(db);
        SeedRunBucket(db, Today.AddDays(-9), "gone-run", 5);
        SeedChatBucket(db, Today.AddDays(-9), 7);
        SeedUsage(db, chat, Today.AddDays(-9), 11);
        db.Context.SaveChanges();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new AnalyticsCleanupService(db.Fresh()).ClearAsync(null, cts.Token));

        await using var check = db.Fresh();
        Assert.Single(check.LoopRunAnalyticsBuckets);
        Assert.Single(check.ChatAnalyticsBuckets);
        Assert.Single(check.ChatUsages);
    }

    private static Guid SeedLiveRun(TestDb db)
    {
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "live-template", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        var node = new LoopNode { Id = Guid.NewGuid(), LoopTemplateVersionId = version.Id, NodeType = NodeType.AI, Label = "ai" };
        var start = Today.AddDays(-2).AddHours(12);
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = $"WI-{Guid.NewGuid():N}",
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Completed,
            StartedAt = start,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        };
        db.Context.LoopTemplates.Add(template);
        db.Context.LoopTemplateVersions.Add(version);
        db.Context.LoopNodes.Add(node);
        db.Context.LoopRuns.Add(run);
        for (var i = 0; i < 2; i++)
        {
            db.Context.LoopRunNodes.Add(new LoopRunNode
            {
                Id = Guid.NewGuid(),
                LoopRunId = run.Id,
                LoopNodeId = node.Id,
                Status = LoopRunNodeStatus.Succeeded,
                StartedAt = start,
                CompletedAt = start.AddSeconds(2),
                InputTokens = 50,
                OutputTokens = 10,
                CostUsd = 0.05m,
                AiProvider = "claude",
            });
        }
        return run.Id;
    }

    private static Guid SeedChat(TestDb db)
    {
        var id = Guid.NewGuid();
        db.Context.ChatSessions.Add(new ChatSession
        {
            Id = id,
            UserId = "tony",
            AiProviderId = Guid.NewGuid(),
            ProviderType = "claude-code",
            ToolAllowlistCsv = "ild",
            ScratchPath = Path.Combine(Path.GetTempPath(), $"ild-cleanup-{id:N}"),
            CreatedAt = DateTime.UtcNow,
        });
        db.Context.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            ChatSessionId = id,
            Role = "user",
            Content = "hello",
            Sequence = 1,
            CreatedAt = DateTime.UtcNow,
        });
        return id;
    }

    private static void SeedUsage(TestDb db, Guid chat, DateTime createdAt, long input) =>
        db.Context.ChatUsages.Add(new ChatUsage
        {
            Id = Guid.NewGuid(),
            ChatSessionId = chat,
            Kind = ChatUsageKind.Turn,
            AiProvider = "claude",
            InputTokens = input,
            OutputTokens = 1,
            CostUsd = 0.01m,
            CreatedAt = createdAt,
        });

    private static void SeedRunBucket(TestDb db, DateTime day, string templateName, long input) =>
        db.Context.LoopRunAnalyticsBuckets.Add(new LoopRunAnalyticsBucket
        {
            Id = Guid.NewGuid(),
            BucketDate = day,
            LoopTemplateId = Guid.NewGuid(),
            TemplateName = templateName,
            AiProvider = "claude",
            RunCount = 1,
            CompletedRuns = 1,
            TotalInputTokens = input,
            TotalOutputTokens = 1,
            TotalCostUsd = 0.01m,
        });

    private static void SeedChatBucket(TestDb db, DateTime day, long input) =>
        db.Context.ChatAnalyticsBuckets.Add(new ChatAnalyticsBucket
        {
            Id = Guid.NewGuid(),
            BucketDate = day,
            LastRecordedAt = day,
            AiProvider = "claude",
            TurnCount = 1,
            TotalInputTokens = input,
            TotalOutputTokens = 1,
            TotalCostUsd = 0.01m,
        });
}
