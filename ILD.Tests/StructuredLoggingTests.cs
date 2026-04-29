using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.InMemory;
using System.Text.Json;

namespace ILD.Tests;

public class StructuredLoggingTests
{
    [Fact]
    public void Log_events_are_structured_json_with_required_fields()
    {
        var sink = new InMemorySink();
        var logger = new LoggerConfiguration()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("Test message {RunId}", Guid.NewGuid());

        var events = sink.LogEvents.ToList();
        events.Should().NotBeEmpty();

        var json = JsonSerializer.Serialize(events[0]);
        json.Should().NotBeNullOrEmpty();

        var parsed = System.Text.Json.JsonDocument.Parse(json);
        parsed.RootElement.TryGetProperty("Timestamp", out _).Should().BeTrue();
        parsed.RootElement.TryGetProperty("Level", out _).Should().BeTrue();
        parsed.RootElement.TryGetProperty("MessageTemplate", out _).Should().BeTrue();
    }

    [Fact]
    public void Put_logging_level_with_valid_level_returns_ok()
    {
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        var controller = new ILD.Api.Controllers.LoggingController(levelSwitch);

        var result = controller.SetLevel(new ILD.Api.Controllers.LoggingController.LogLevelRequest { Level = "Debug" });

        result.Should().BeOfType<OkObjectResult>();
        levelSwitch.MinimumLevel.Should().Be(LogEventLevel.Debug);
    }

    [Fact]
    public void Put_logging_level_with_invalid_level_returns_bad_request()
    {
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        var controller = new ILD.Api.Controllers.LoggingController(levelSwitch);

        var result = controller.SetLevel(new ILD.Api.Controllers.LoggingController.LogLevelRequest { Level = "Invalid" });

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
