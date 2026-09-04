using ILD.Api.Configuration;
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
    private readonly StartupLogLevel _startupLevel;

    public LoggingController(LoggingLevelSwitch levelSwitch, StartupLogLevel startupLevel)
    {
        _levelSwitch = levelSwitch;
        _startupLevel = startupLevel;
    }

    [HttpGet("level")]
    public IActionResult GetLevel()
    {
        return Ok(new LogLevelResponse
        {
            Level = _levelSwitch.MinimumLevel.ToString(),
            StartupLevel = _startupLevel.Level.ToString(),
            IsOverride = _levelSwitch.MinimumLevel != _startupLevel.Level,
        });
    }

    [HttpPut("level")]
    public IActionResult SetLevel([FromBody] LogLevelRequest request)
    {
        if (!Enum.TryParse<LogEventLevel>(request.Level, true, out var level))
        {
            return BadRequest(new { error = "Invalid log level", message = $"Valid values: {string.Join(", ", Enum.GetNames<LogEventLevel>())}" });
        }

        _levelSwitch.MinimumLevel = level;

        return Ok(new LogLevelResponse
        {
            Level = level.ToString(),
            StartupLevel = _startupLevel.Level.ToString(),
            IsOverride = level != _startupLevel.Level,
        });
    }

    public class LogLevelRequest
    {
        public string Level { get; set; } = string.Empty;
    }

    public class LogLevelResponse
    {
        /// <summary>What the process is logging at right now.</summary>
        public string Level { get; set; } = string.Empty;

        /// <summary>What ILD_LOG_LEVEL set at startup, and what a restart returns to.</summary>
        public string StartupLevel { get; set; } = string.Empty;

        public bool IsOverride { get; set; }
    }
}
