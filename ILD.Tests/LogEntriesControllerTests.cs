using ILD.Api.Configuration;
using ILD.Api.Controllers;
using ILD.Data.DTOs.SignalRPayloads;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ILD.Tests;

public class LogEntriesControllerTests
{
    private static (LogEntriesController Controller, ILogger Logger) Build()
    {
        var buffer = new LogEntryBuffer(500);
        var logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(new LoggingLevelSwitch(LogEventLevel.Debug))
            .WriteTo.Sink(buffer)
            .CreateLogger();
        return (new LogEntriesController(buffer), logger);
    }

    private static IReadOnlyList<LogEntryPayload> Body(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsAssignableFrom<IReadOnlyList<LogEntryPayload>>(ok.Value);
    }

    [Fact]
    public void A_minimum_level_and_a_search_narrow_what_comes_back()
    {
        var (controller, logger) = Build();
        logger.Debug("a debug line about hosts");
        logger.Error("an error about hosts");
        logger.Error("an unrelated error");

        Assert.Equal(2, Body(controller.GetEntries(minimumLevel: "error")).Count);
        Assert.Equal(2, Body(controller.GetEntries(search: "hosts")).Count);
        Assert.Equal(
            ["an error about hosts"],
            Body(controller.GetEntries(minimumLevel: "Error", search: "hosts")).Select(e => e.Message));
    }

    [Fact]
    public void An_unknown_minimum_level_is_a_bad_request()
    {
        var (controller, _) = Build();

        Assert.IsType<BadRequestObjectResult>(controller.GetEntries(minimumLevel: "loud"));
    }
}
