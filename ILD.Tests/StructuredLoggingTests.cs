using Microsoft.AspNetCore.Mvc;
using Serilog.Core;
using Serilog.Events;

namespace ILD.Tests;

public class StructuredLoggingTests
{
    [Fact]
    public void Put_logging_level_with_valid_level_returns_ok()
    {
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        var controller = new ILD.Api.Controllers.LoggingController(levelSwitch, new ILD.Api.Configuration.StartupLogLevel(LogEventLevel.Information));

        var result = controller.SetLevel(new ILD.Api.Controllers.LoggingController.LogLevelRequest { Level = "Debug" });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(LogEventLevel.Debug, levelSwitch.MinimumLevel);
    }

    [Fact]
    public void Put_logging_level_with_invalid_level_returns_bad_request()
    {
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        var controller = new ILD.Api.Controllers.LoggingController(levelSwitch, new ILD.Api.Configuration.StartupLogLevel(LogEventLevel.Information));

        var result = controller.SetLevel(new ILD.Api.Controllers.LoggingController.LogLevelRequest { Level = "Invalid" });

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
