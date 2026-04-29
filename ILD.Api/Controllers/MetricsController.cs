using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

[ApiController]
[Route("metrics")]
public class MetricsController : ControllerBase
{
    private readonly IMetricsCollector _collector;

    public MetricsController(IMetricsCollector collector)
    {
        _collector = collector;
    }

    [HttpGet]
    public IActionResult Get()
    {
        return Content(_collector.Snapshot(), "text/plain; version=0.0.4; charset=utf-8");
    }
}
