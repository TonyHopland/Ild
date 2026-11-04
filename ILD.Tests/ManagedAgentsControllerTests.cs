using ILD.Api.Controllers;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ILD.Tests;

public class ManagedAgentsControllerTests
{
    private static ManagedAgentStatus Status(string key, string? installed, string? latest)
        => new(key, key, "pkg-" + key, installed, latest, latest is not null, null);

    [Fact]
    public async Task Rollback_returns_the_refreshed_status()
    {
        var service = new Mock<IManagedAgentService>();
        service.Setup(s => s.RollbackAsync("pi", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status("pi", "1.0.0", "2.0.0"));
        var controller = new ManagedAgentsController(service.Object);

        var result = await controller.Rollback("pi", null, CancellationToken.None);

        Assert.Equal("1.0.0", Assert.IsType<ManagedAgentStatus>(Assert.IsType<OkObjectResult>(result).Value).InstalledVersion);
    }

    [Fact]
    public async Task Rollback_passes_on_the_version_the_caller_was_shown()
    {
        var service = new Mock<IManagedAgentService>();
        service.Setup(s => s.RollbackAsync("pi", "0.9.0", It.IsAny<CancellationToken>())).ReturnsAsync(Status("pi", "0.9.0", "1.0.0"));
        var controller = new ManagedAgentsController(service.Object);

        Assert.IsType<OkObjectResult>(
            await controller.Rollback("pi", new ManagedAgentsController.RollbackRequest("0.9.0"), CancellationToken.None));
        service.Verify(s => s.RollbackAsync("pi", "0.9.0", It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Rollback_returns_404_for_unknown_agent()
    {
        var controller = new ManagedAgentsController(new Mock<IManagedAgentService>().Object);

        Assert.IsType<NotFoundObjectResult>(await controller.Rollback("nope", null, CancellationToken.None));
    }

    [Fact]
    public async Task Rollback_without_a_previous_version_is_a_409_naming_the_reason()
    {
        var service = new Mock<IManagedAgentService>();
        service.Setup(s => s.RollbackAsync("pi", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Pi has no previous version to roll back to."));
        var controller = new ManagedAgentsController(service.Object);

        var result = Assert.IsType<ConflictObjectResult>(await controller.Rollback("pi", null, CancellationToken.None));

        Assert.Contains("no previous version", result.Value!.ToString());
    }

    [Fact]
    public async Task Update_returns_404_for_unknown_agent()
    {
        var service = new Mock<IManagedAgentService>();
        var controller = new ManagedAgentsController(service.Object);

        var result = await controller.Update("nope", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        service.Verify(s => s.UpdateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_returns_502_when_install_fails()
    {
        var service = new Mock<IManagedAgentService>();
        service.Setup(s => s.UpdateAsync("pi", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("npm boom"));
        var controller = new ManagedAgentsController(service.Object);

        var result = await controller.Update("pi", CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
    }
}
