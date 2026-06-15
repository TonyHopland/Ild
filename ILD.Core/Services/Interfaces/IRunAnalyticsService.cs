using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Aggregates the run data ILD already collects (run/node timing, edge
/// attribution, event log, token/cost) into the per-template figures the
/// analytics dashboard renders. Read-only over the existing schema.
/// </summary>
public interface IRunAnalyticsService
{
    Task<RunAnalyticsOverview> GetOverviewAsync(CancellationToken cancellationToken = default);
}
