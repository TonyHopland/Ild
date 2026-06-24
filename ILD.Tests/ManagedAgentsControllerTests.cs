using ILD.Api.Controllers;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ILD.Tests;

public class ManagedAgentsControllerTests
{
    private static ManagedAgentStatus Status(string key, bool updateAvailable = true)
        => new(key, key, $"{key}-pkg", "1.0.0", "1.0.1", updateAvailable, null);

    [Fact]
    public async Task GetAll_returns_statuses()
    {
        var service = new Mock<IManagedAgentService>();
        service.Setup(s => s.GetStatusesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Status("pi"), Status("opencode")]);
        var controller = new ManagedAgentsController(service.Object);

        var result = await controller.GetAll(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var statuses = Assert.IsAssignableFrom<IReadOnlyList<ManagedAgentStatus>>(ok.Value);
        Assert.Equal(2, statuses.Count);
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
    public async Task Update_returns_refreshed_status_on_success()
    {
        var service = new Mock<IManagedAgentService>();
        service.Setup(s => s.UpdateAsync("pi", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Status("pi", updateAvailable: false));
        var controller = new ManagedAgentsController(service.Object);

        var result = await controller.Update("pi", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var status = Assert.IsType<ManagedAgentStatus>(ok.Value);
        Assert.False(status.UpdateAvailable);
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
