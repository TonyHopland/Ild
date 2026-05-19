namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Unified work-item scheduler. Combines remote polling with on-demand
/// "pulse" wakeups triggered by local events (work-item Done, settings
/// changed, etc.) so claims and resumes happen promptly instead of waiting
/// for the next poll interval.
/// </summary>
public interface IWorkItemScheduler
{
    /// <summary>
    /// Wake the scheduler immediately. Coalesced — repeated calls before the
    /// next pass collapse to a single pass.
    /// </summary>
    void Pulse();
}
