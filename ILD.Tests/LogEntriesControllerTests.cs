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
    private static (LogEntriesController Controller, ILogger Logger) Build(int capacity = 500)
    {
        var buffer = new LogEntryBuffer(capacity);
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
    public void An_empty_buffer_answers_with_no_entries()
    {
        var (controller, _) = Build();

        Assert.Empty(Body(controller.GetEntries()));
    }

    [Fact]
    public void The_entries_come_back_newest_first_with_what_the_page_renders()
    {
        var (controller, logger) = Build();
        logger.Information("older");
        logger.ForContext("SourceContext", "ILD.Core.LoopEngine").Warning("newer");

        var entries = Body(controller.GetEntries());

        Assert.Equal(["newer", "older"], entries.Select(e => e.Message));
        Assert.Equal("Warning", entries[0].Level);
        Assert.Equal("ILD.Core.LoopEngine", entries[0].Source);
        Assert.NotEqual(default, entries[0].Timestamp);
    }

    [Fact]
    public void Take_pages_the_newest_entries_and_never_exceeds_the_buffer()
    {
        var (controller, logger) = Build(capacity: 4);
        for (var i = 1; i <= 6; i++) logger.Information("line {N}", i);

        Assert.Equal(["line 6", "line 5"], Body(controller.GetEntries(take: 2)).Select(e => e.Message));
        Assert.Equal(4, Body(controller.GetEntries(take: 1000)).Count);
        Assert.Single(Body(controller.GetEntries(take: 0)));
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
