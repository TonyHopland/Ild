using System.Net;
using System.Net.Http.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Moq;

namespace ILD.Tests.Integration;

public class ManagedAgentRollbackApiTests
{
    private static ApiFactory Host(Mock<IManagedAgentService> agents)
        => new(configureServices: services => services.ReplaceSingleton(agents.Object));

    [Fact]
    public async Task A_rollback_with_no_body_or_with_the_shown_version_reaches_the_service()
    {
        var agents = new Mock<IManagedAgentService>();
        agents.Setup(a => a.RollbackAsync("pi", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedAgentStatus("pi", "Pi", "pkg", "1.0.0", "2.0.0", true, null));
        await using var factory = Host(agents);
        var client = await factory.CreateAuthenticatedClientAsync();

        using (var bare = await client.PostAsync("/api/v1/managedagents/pi/rollback", null, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.OK, bare.StatusCode);
        agents.Verify(a => a.RollbackAsync("pi", null, It.IsAny<CancellationToken>()), Times.Once());

        using (var shown = await client.PostAsJsonAsync("/api/v1/managedagents/pi/rollback", new { previousVersion = "1.0.0" }, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        agents.Verify(a => a.RollbackAsync("pi", "1.0.0", It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task A_rollback_with_nothing_to_roll_back_to_is_a_409_and_an_unknown_agent_a_404()
    {
        var agents = new Mock<IManagedAgentService>();
        agents.Setup(a => a.RollbackAsync("pi", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Pi has no previous version to roll back to."));
        await using var factory = Host(agents);
        var client = await factory.CreateAuthenticatedClientAsync();

        using var conflict = await client.PostAsync("/api/v1/managedagents/pi/rollback", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("no previous version", await conflict.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var unknown = await client.PostAsync("/api/v1/managedagents/nope/rollback", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
