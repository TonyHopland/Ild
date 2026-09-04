using ILD.Api.Configuration;
using ILD.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Serilog.Core;
using Serilog.Events;

namespace ILD.Tests;

public class LoggingControllerTests
{
    private static (LoggingController Controller, LoggingLevelSwitch Switch) Build(
        LogEventLevel startup = LogEventLevel.Information)
    {
        var levelSwitch = new LoggingLevelSwitch(startup);
        return (new LoggingController(levelSwitch, new StartupLogLevel(startup)), levelSwitch);
    }

    private static LoggingController.LogLevelResponse Body(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<LoggingController.LogLevelResponse>(ok.Value);
    }

    [Fact]
    public void GetLevel_reports_the_startup_level_as_no_override()
    {
        var (controller, _) = Build();

        var body = Body(controller.GetLevel());

        Assert.Equal("Information", body.Level);
        Assert.Equal("Information", body.StartupLevel);
        Assert.False(body.IsOverride);
    }

    [Fact]
    public void GetLevel_reads_back_the_level_that_was_set()
    {
        var (controller, levelSwitch) = Build();

        controller.SetLevel(new LoggingController.LogLevelRequest { Level = "Debug" });

        Assert.Equal(LogEventLevel.Debug, levelSwitch.MinimumLevel);
        var body = Body(controller.GetLevel());
        Assert.Equal("Debug", body.Level);
        Assert.Equal("Information", body.StartupLevel);
        Assert.True(body.IsOverride);
    }

    [Fact]
    public void SetLevel_back_to_the_startup_level_is_no_longer_an_override()
    {
        var (controller, _) = Build(LogEventLevel.Warning);

        controller.SetLevel(new LoggingController.LogLevelRequest { Level = "Debug" });
        var body = Body(controller.SetLevel(new LoggingController.LogLevelRequest { Level = "Warning" }));

        Assert.Equal("Warning", body.Level);
        Assert.False(body.IsOverride);
    }

    [Fact]
    public void SetLevel_accepts_a_level_in_any_casing()
    {
        var (controller, levelSwitch) = Build();

        controller.SetLevel(new LoggingController.LogLevelRequest { Level = "warning" });

        Assert.Equal(LogEventLevel.Warning, levelSwitch.MinimumLevel);
    }

    [Fact]
    public void SetLevel_refuses_an_unknown_level_and_leaves_the_switch_alone()
    {
        var (controller, levelSwitch) = Build();

        var result = controller.SetLevel(new LoggingController.LogLevelRequest { Level = "Chatty" });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(LogEventLevel.Information, levelSwitch.MinimumLevel);
    }
}
