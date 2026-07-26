using System.ComponentModel.DataAnnotations;

namespace ILD.Data.Entities;

/// <summary>
/// A durable record of one pull request opened against a work item, written
/// when the <c>LoopRun</c> that carried it is deleted (manual delete or the
/// retention sweeper) so the link survives the run row's removal — otherwise a
/// work item would silently lose its PRs as its runs are reclaimed (WI-203).
///
/// The same shape as the analytics rollup (<see cref="LoopRunAnalyticsBucket"/>)
/// and for the same reason: the work item view reads these records <em>plus</em>
/// the PRs still on live runs, so a PR is sourced from exactly one place (the
/// run while it lives, this record afterwards) and nothing has to be kept in
/// sync while the run exists. Overlap is harmless regardless — the projection
/// deduplicates by <see cref="Url"/>.
///
/// One row per (work item, PR URL): a retried run pointed back at its
/// predecessor's PR folds into the same record rather than duplicating it.
/// </summary>
public class WorkItemPullRequestRecord
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string WorkItemId { get; set; } = string.Empty;

    /// <summary>The PR's URL — the identity a work item's PR history is deduplicated on.</summary>
    [Required]
    [MaxLength(2048)]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// The run this was last observed on, kept for provenance. The row it names
    /// is gone by construction (that deletion is what wrote this record), so the
    /// view projects it as null unless the id still resolves to a live run.
    /// </summary>
    public Guid? LoopRunId { get; set; }

    /// <summary>
    /// Whether the PR was ever observed merged. Sticky: a later run pointed at
    /// the same PR cannot un-merge it.
    /// </summary>
    public bool Merged { get; set; }

    /// <summary>
    /// The last <c>RemotePrSnapshot</c> the heartbeat poller captured for this
    /// PR (JSON, as stored on <see cref="LoopRun.PrSnapshot"/>), so the badge
    /// state outlives the run. Null when the PR was never polled.
    /// </summary>
    public string? PrSnapshot { get; set; }

    /// <summary>
    /// When this PR was first seen on the work item, taken as the start of the
    /// earliest run carrying it. Surfaced as the history entry's created time.
    /// </summary>
    public DateTime FirstSeenAt { get; set; }

    /// <summary>
    /// The start of the most recent run carrying this PR. Orders the work
    /// item's PR history (newest run first) once the runs themselves are gone,
    /// and decides which run's merge flag and snapshot win.
    /// </summary>
    public DateTime LastSeenAt { get; set; }
}
