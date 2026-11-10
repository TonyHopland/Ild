using ILD.Api.Configuration;
using ILD.Api.Controllers;
using System.Net;
using ILD.Core.Services.Implementations.Network;
using Microsoft.AspNetCore.Http;
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
        return (new LoggingController(levelSwitch, new StartupLogLevel(startup), new IsolatedSource()), levelSwitch);
    }

    internal sealed class IsolatedSource : IProtectedDestinationSource
    {
        public ValueTask<IReadOnlyList<ProtectedDestination>> GetAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ProtectedDestination>>([]);

        public ValueTask<IReadOnlyList<IpRange>> GetIsolatedRangesAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<IpRange>>([IpRange.Parse("10.88.0.0/16")]);
    }

    private static void CallFrom(LoggingController controller, string address)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(address);
        controller.ControllerContext = new() { HttpContext = http };
    }

    [Theory]
    [InlineData("10.88.3.4")]
    [InlineData("::ffff:10.88.3.4")]
    public async Task SetLevel_is_refused_from_a_run_network(string address)
    {
        var (controller, levelSwitch) = Build();
        CallFrom(controller, address);

        var result = await controller.SetLevel(new LoggingController.LogLevelRequest { Level = "Fatal" }, TestContext.Current.CancellationToken);

        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(LogEventLevel.Information, levelSwitch.MinimumLevel);
    }

    [Fact]
    public async Task SetLevel_is_allowed_from_outside_the_run_networks()
    {
        var (controller, levelSwitch) = Build();
        CallFrom(controller, "192.168.1.20");

        Body(await controller.SetLevel(new LoggingController.LogLevelRequest { Level = "Debug" }, TestContext.Current.CancellationToken));

        Assert.Equal(LogEventLevel.Debug, levelSwitch.MinimumLevel);
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
    public async Task GetLevel_reads_back_the_level_that_was_set()
    {
        var (controller, levelSwitch) = Build();

        await controller.SetLevel(new LoggingController.LogLevelRequest { Level = "Debug" }, TestContext.Current.CancellationToken);

        Assert.Equal(LogEventLevel.Debug, levelSwitch.MinimumLevel);
        var body = Body(controller.GetLevel());
        Assert.Equal("Debug", body.Level);
        Assert.Equal("Information", body.StartupLevel);
        Assert.True(body.IsOverride);
    }

    [Fact]
    public async Task SetLevel_back_to_the_startup_level_is_no_longer_an_override()
    {
        var (controller, _) = Build(LogEventLevel.Warning);

        await controller.SetLevel(new LoggingController.LogLevelRequest { Level = "Debug" }, TestContext.Current.CancellationToken);
        var body = Body(await controller.SetLevel(new LoggingController.LogLevelRequest { Level = "Warning" }, TestContext.Current.CancellationToken));

        Assert.Equal("Warning", body.Level);
        Assert.False(body.IsOverride);
    }

    [Fact]
    public async Task SetLevel_accepts_a_level_in_any_casing()
    {
        var (controller, levelSwitch) = Build();

        await controller.SetLevel(new LoggingController.LogLevelRequest { Level = "warning" }, TestContext.Current.CancellationToken);

        Assert.Equal(LogEventLevel.Warning, levelSwitch.MinimumLevel);
    }

    [Fact]
    public async Task SetLevel_refuses_an_unknown_level_and_leaves_the_switch_alone()
    {
        var (controller, levelSwitch) = Build();

        var result = await controller.SetLevel(new LoggingController.LogLevelRequest { Level = "Chatty" }, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(LogEventLevel.Information, levelSwitch.MinimumLevel);
    }
}
