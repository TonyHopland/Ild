using ILD.Api.Configuration;
using ILD.Data.DTOs.SignalRPayloads;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ILD.Tests;

public class LogEntryBufferTests
{
    private static (ILogger Logger, LogEntryBuffer Buffer, LoggingLevelSwitch Switch) Build(
        int capacity = 500,
        LogEventLevel level = LogEventLevel.Information)
    {
        var levelSwitch = new LoggingLevelSwitch(level);
        var buffer = new LogEntryBuffer(capacity);
        var logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .WriteTo.Sink(buffer)
            .CreateLogger();
        return (logger, buffer, levelSwitch);
    }

    [Fact]
    public void The_newest_entries_are_read_back_first()
    {
        var (logger, buffer, _) = Build();

        logger.Information("first");
        logger.Information("second");

        Assert.Equal(["second", "first"], buffer.Entries(10).Select(e => e.Message));
    }

    [Fact]
    public void Nothing_beyond_the_capacity_is_kept()
    {
        var (logger, buffer, _) = Build(capacity: 3);

        for (var i = 1; i <= 5; i++) logger.Information("line {N}", i);

        var entries = buffer.Entries(100);
        Assert.Equal(["line 5", "line 4", "line 3"], entries.Select(e => e.Message));
        Assert.Equal([5L, 4L, 3L], entries.Select(e => e.Id));
    }

    [Fact]
    public void Take_bounds_how_many_come_back()
    {
        var (logger, buffer, _) = Build();

        for (var i = 1; i <= 10; i++) logger.Information("line {N}", i);

        Assert.Equal(["line 10", "line 9"], buffer.Entries(2).Select(e => e.Message));
        Assert.Empty(buffer.Entries(0));
    }

    [Fact]
    public void A_level_changed_while_running_decides_what_reaches_the_buffer()
    {
        var (logger, buffer, levelSwitch) = Build();

        logger.Debug("too quiet");
        levelSwitch.MinimumLevel = LogEventLevel.Debug;
        logger.Debug("loud enough");
        levelSwitch.MinimumLevel = LogEventLevel.Error;
        logger.Warning("quiet again");

        Assert.Equal(["loud enough"], buffer.Entries(10).Select(e => e.Message));
    }

    [Fact]
    public void An_entry_carries_its_level_source_and_exception()
    {
        var (logger, buffer, _) = Build();

        logger
            .ForContext("SourceContext", "ILD.Core.LoopEngine")
            .Error(new InvalidOperationException("boom"), "Run {RunId} failed", "7f21");

        var entry = Assert.Single(buffer.Entries(10));
        Assert.Equal("Error", entry.Level);
        Assert.Equal("ILD.Core.LoopEngine", entry.Source);
        Assert.Equal("Run \"7f21\" failed", entry.Message);
        Assert.Contains("InvalidOperationException: boom", entry.Detail);
    }

    [Fact]
    public void An_event_without_a_source_context_still_reads_back()
    {
        var (logger, buffer, _) = Build();

        logger.Information("Database ready");

        var entry = Assert.Single(buffer.Entries(10));
        Assert.Equal(string.Empty, entry.Source);
        Assert.Null(entry.Detail);
    }

    [Fact]
    public void A_minimum_level_hides_everything_below_it()
    {
        var (logger, buffer, levelSwitch) = Build(level: LogEventLevel.Debug);

        logger.Debug("a debug line");
        logger.Warning("a warning line");
        logger.Error("an error line");
        Assert.Equal(LogEventLevel.Debug, levelSwitch.MinimumLevel);

        Assert.Equal(
            ["an error line", "a warning line"],
            buffer.Entries(10, LogEventLevel.Warning).Select(e => e.Message));
    }

    [Fact]
    public void A_search_matches_the_message_or_the_source()
    {
        var (logger, buffer, _) = Build();

        logger.ForContext("SourceContext", "ILD.Core.Network.EgressProxy").Information("CONNECT blocked");
        logger.ForContext("SourceContext", "ILD.Core.LoopEngine").Information("entered node");

        Assert.Equal(["CONNECT blocked"], buffer.Entries(10, search: "egressproxy").Select(e => e.Message));
        Assert.Equal(["entered node"], buffer.Entries(10, search: "NODE").Select(e => e.Message));
        Assert.Empty(buffer.Entries(10, search: "nothing here"));
    }

    [Fact]
    public void Every_appended_entry_is_announced_once()
    {
        var (logger, buffer, _) = Build();
        var announced = new List<LogEntryPayload>();
        buffer.Appended = announced.Add;

        logger.Information("first");
        logger.Information("second");

        Assert.Equal(["first", "second"], announced.Select(e => e.Message));
    }

    /// <summary>
    /// The push travels over SignalR, so the events SignalR writes while pushing
    /// must not be pushed in turn — that is a viewer feeding itself. They are
    /// still buffered, and read back on the next request like anything else.
    /// </summary>
    [Theory]
    [InlineData("Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher", null)]
    [InlineData("Microsoft.AspNetCore.Http.Connections.Internal.HttpConnectionManager", null)]
    [InlineData("Serilog.AspNetCore.RequestLoggingMiddleware", "/hubs/work-item")]
    public void The_traffic_that_carries_an_announcement_is_buffered_but_never_announced(string source, string? requestPath)
    {
        var (logger, buffer, _) = Build();
        var announced = new List<LogEntryPayload>();
        buffer.Appended = announced.Add;

        var context = logger.ForContext("SourceContext", source);
        if (requestPath is not null) context = context.ForContext("RequestPath", requestPath);
        context.Information("chatter");

        Assert.Empty(announced);
        Assert.Equal("chatter", Assert.Single(buffer.Entries(10)).Message);
    }

    [Fact]
    public void A_request_that_is_not_the_hubs_is_announced()
    {
        var (logger, buffer, _) = Build();
        var announced = new List<LogEntryPayload>();
        buffer.Appended = announced.Add;

        logger
            .ForContext("SourceContext", "Serilog.AspNetCore.RequestLoggingMiddleware")
            .ForContext("RequestPath", "/api/v1/loopruns")
            .Information("HTTP GET responded 200");

        Assert.Single(announced);
    }

    [Fact]
    public void A_failing_announcement_never_reaches_the_caller()
    {
        var (logger, buffer, _) = Build();
        buffer.Appended = _ => throw new InvalidOperationException("hub is down");

        logger.Information("still logged");

        Assert.Single(buffer.Entries(10));
    }
}
