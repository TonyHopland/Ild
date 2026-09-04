using ILD.Api.Configuration;
using Microsoft.AspNetCore.Mvc;
using Serilog.Events;

namespace ILD.Api.Controllers;

/// <summary>
/// The tail of the process's own log, for the Logging settings page.
///
/// Deliberately not an action on <see cref="LoggingController"/>: that class is
/// [AllowAnonymous] so an operator who cannot sign in can still turn the level
/// up, and a class-level AllowAnonymous cannot be tightened again per action.
/// Log lines carry hostnames, tokens and user data, so reading them is left to
/// the user-only fallback policy — see
/// <see cref="Authentication.IldAuthentication"/>.
/// </summary>
[ApiController]
[Route("api/v1/logging")]
public class LogEntriesController : ControllerBase
{
    private const int DefaultTake = 200;

    private readonly LogEntryBuffer _buffer;

    public LogEntriesController(LogEntryBuffer buffer)
    {
        _buffer = buffer;
    }

    [HttpGet("entries")]
    public IActionResult GetEntries(
        [FromQuery] int take = DefaultTake,
        [FromQuery] string? minimumLevel = null,
        [FromQuery] string? search = null)
    {
        LogEventLevel? floor = null;
        if (!string.IsNullOrWhiteSpace(minimumLevel))
        {
            if (!Enum.TryParse<LogEventLevel>(minimumLevel, true, out var parsed))
                return BadRequest(new
                {
                    error = "Invalid log level",
                    message = $"Valid values: {string.Join(", ", Enum.GetNames<LogEventLevel>())}",
                });
            floor = parsed;
        }

        return Ok(_buffer.Entries(Math.Clamp(take, 0, _buffer.Capacity), floor, search));
    }
}
