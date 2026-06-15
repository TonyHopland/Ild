using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

/// <summary>
/// A durable, pre-aggregated rollup of one day's runs for a (template,
/// AI-provider) pair. Written when a <c>LoopRun</c> is deleted (manual delete or
/// the retention sweeper) so its token/cost/timing history survives the run
/// row's removal — otherwise the analytics dashboard would silently under-report
/// as runs are reclaimed. The analytics service reads these buckets <em>plus</em>
/// the still-live runs; a run contributes to exactly one source (it is live
/// until deleted, then archived here), so the two never double-count.
/// All metric columns are additive across runs.
/// </summary>
public class LoopRunAnalyticsBucket
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>The run's start day, truncated to UTC midnight. Rolled up to week/month/year at read time.</summary>
    public DateTime BucketDate { get; set; }

    public Guid LoopTemplateId { get; set; }

    /// <summary>Denormalized so the figure survives the template being deleted/renamed.</summary>
    [MaxLength(256)]
    public string TemplateName { get; set; } = string.Empty;

    /// <summary>The run's primary AI provider name, or <c>"(none)"</c> when it used no AI provider.</summary>
    [MaxLength(256)]
    public string AiProvider { get; set; } = string.Empty;

    public int RunCount { get; set; }
    public int CompletedRuns { get; set; }
    public int FailedRuns { get; set; }
    public int CancelledRuns { get; set; }

    public int NodeCount { get; set; }
    public double TotalNodeSeconds { get; set; }

    public int OnFailureRoutings { get; set; }
    public int RejectRoutings { get; set; }

    public int FeedbackCount { get; set; }
    public double TotalFeedbackSeconds { get; set; }

    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public decimal TotalCostUsd { get; set; }
}
