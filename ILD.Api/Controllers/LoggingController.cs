using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serilog.Core;
using Serilog.Events;

namespace ILD.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
// Turning the log level up is an operator's first move when the app is too
// broken to log in; it has never required a session and still does not.
[AllowAnonymous]
public class LoggingController : ControllerBase
{
    private readonly LoggingLevelSwitch _levelSwitch;

    public LoggingController(LoggingLevelSwitch levelSwitch)
    {
        _levelSwitch = levelSwitch;
    }

    [HttpPut("level")]
    public IActionResult SetLevel([FromBody] LogLevelRequest request)
    {
        if (!Enum.TryParse<LogEventLevel>(request.Level, true, out var level))
        {
            return BadRequest(new { error = "Invalid log level", message = $"Valid values: {string.Join(", ", Enum.GetNames<LogEventLevel>())}" });
        }

        _levelSwitch.MinimumLevel = level;

        return Ok(new { level = request.Level });
    }

    public class LogLevelRequest
    {
        public string Level { get; set; } = string.Empty;
    }
}
