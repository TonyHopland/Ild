using ILD.Core.Services.Interfaces;
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
    /// Account-wide token/cost totals plus a per-loop-template breakdown of
    /// success rate, time-per-node, routing, human-feedback turnaround, and
    /// token/cost — the data the run analytics dashboard renders.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetOverview(CancellationToken cancellationToken)
    {
        var overview = await _analytics.GetOverviewAsync(cancellationToken);
        return Ok(overview);
    }
}
