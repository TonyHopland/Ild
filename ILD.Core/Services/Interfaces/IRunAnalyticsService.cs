using ILD.Data.DTOs;

namespace ILD.Core.Services.Interfaces;

/// <summary>
/// Aggregates the run data ILD already collects (run/node timing, edge
/// attribution, event log, token/cost) into the per-template, per-provider, and
/// time-series figures the analytics dashboard renders. Merges still-live runs
/// with the durable archive of reclaimed runs, then applies the query's filters.
/// </summary>
public interface IRunAnalyticsService
{
    Task<RunAnalyticsOverview> GetOverviewAsync(AnalyticsQuery query, CancellationToken cancellationToken = default);
}
