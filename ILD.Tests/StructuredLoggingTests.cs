using Microsoft.AspNetCore.Mvc;
using Serilog.Core;
using Serilog.Events;

namespace ILD.Tests;

public class StructuredLoggingTests
{
    [Fact]
    public async Task Put_logging_level_with_valid_level_returns_ok()
    {
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        var controller = new ILD.Api.Controllers.LoggingController(levelSwitch, new ILD.Api.Configuration.StartupLogLevel(LogEventLevel.Information), new LoggingControllerTests.IsolatedSource());

        var result = await controller.SetLevel(new ILD.Api.Controllers.LoggingController.LogLevelRequest { Level = "Debug" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(LogEventLevel.Debug, levelSwitch.MinimumLevel);
    }

    [Fact]
    public async Task Put_logging_level_with_invalid_level_returns_bad_request()
    {
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        var controller = new ILD.Api.Controllers.LoggingController(levelSwitch, new ILD.Api.Configuration.StartupLogLevel(LogEventLevel.Information), new LoggingControllerTests.IsolatedSource());

        var result = await controller.SetLevel(new ILD.Api.Controllers.LoggingController.LogLevelRequest { Level = "Invalid" }, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
