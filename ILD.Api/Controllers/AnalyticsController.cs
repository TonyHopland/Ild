using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class AnalyticsController : ControllerBase
{
    private readonly IRunAnalyticsService _analytics;

    public AnalyticsController(IRunAnalyticsService analytics)
    {
        _analytics = analytics;
    }

    /// <summary>
    /// Account-wide token/cost totals plus per-loop-template and per-provider
    /// breakdowns and a time series — the data the run analytics dashboard
    /// renders. Optional filters: <c>from</c>/<c>to</c> (yyyy-MM-dd, inclusive,
    /// on the run's start day), <c>provider</c> (agent provider name), and
    /// <c>granularity</c> (day|week|month|year) for the series buckets.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetOverview(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? provider,
        [FromQuery] string? granularity,
        CancellationToken cancellationToken)
    {
        if (!TryParseDate(from, out var fromDate))
            return BadRequest(new { error = "Invalid 'from' date; expected yyyy-MM-dd." });
        if (!TryParseDate(to, out var toDate))
            return BadRequest(new { error = "Invalid 'to' date; expected yyyy-MM-dd." });
        if (!TryParseGranularity(granularity, out var gran))
            return BadRequest(new { error = "Invalid 'granularity'; expected day, week, month, or year." });

        var query = new AnalyticsQuery(
            fromDate,
            toDate,
            string.IsNullOrWhiteSpace(provider) ? null : provider,
            gran);
        var overview = await _analytics.GetOverviewAsync(query, cancellationToken);
        return Ok(overview);
    }

    private static bool TryParseDate(string? value, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (DateOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            date = parsed;
            return true;
        }
        return false;
    }

    private static bool TryParseGranularity(string? value, out AnalyticsGranularity granularity)
    {
        granularity = AnalyticsGranularity.Day;
        if (string.IsNullOrWhiteSpace(value)) return true;
        return Enum.TryParse(value, ignoreCase: true, out granularity);
    }
}
