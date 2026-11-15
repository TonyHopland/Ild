using System.Net;
using System.Net.Http.Json;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests.Integration;

/// <summary>POST /api/v1/loopruns/{id}/failed-node/retry and /resume: the actions on a node that failed, such as an out-of-memory kill.</summary>
public class FailedNodeActionApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(ApiFactory Factory, HttpClient Client, Mock<ILoopEngine> Engine)> HostAsync()
    {
        var engine = new Mock<ILoopEngine>();
        var factory = new ApiFactory(configureServices: services =>
        {
            services.ReplaceSingleton(new Mock<IWorkItemNotifier>().Object);
            services.ReplaceSingleton(new Mock<IChatNotifier>().Object);
            services.ReplaceSingleton(engine.Object);
        });
        return (factory, await factory.CreateAuthenticatedClientAsync(), engine);
    }

    [Fact]
    public async Task Retry_and_resume_pass_the_note_and_the_user_to_the_engine()
    {
        var (factory, client, engine) = await HostAsync();
        await using var _ = factory;
        var run = Guid.NewGuid();

        var retry = await client.PostAsJsonAsync($"/api/v1/loopruns/{run}/failed-node/retry", new { note = "one project at a time" }, Ct);
        var resume = await client.PostAsJsonAsync($"/api/v1/loopruns/{run}/failed-node/resume", new { note = (string?)null }, Ct);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        engine.Verify(e => e.RetryAfterFailureAsync(run, "one project at a time", It.Is<string>(u => u.Length > 0)), Times.Once);
        engine.Verify(e => e.ResumeAfterFailureAsync(run, null, It.Is<string>(u => u.Length > 0)), Times.Once);
    }

    [Theory]
    [InlineData("retry")]
    [InlineData("resume")]
    public async Task A_run_that_is_not_parked_on_a_failed_node_or_cannot_be_resumed_is_refused_with_the_reason(string action)
    {
        var (factory, client, engine) = await HostAsync();
        await using var _ = factory;
        engine.Setup(e => e.RetryAfterFailureAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("The run is not parked on a failed node"));
        engine.Setup(e => e.ResumeAfterFailureAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("Cannot resume this attempt: no session recorded"));

        var response = await client.PostAsJsonAsync($"/api/v1/loopruns/{Guid.NewGuid()}/failed-node/{action}", new { note = (string?)null }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(action == "retry" ? "not parked on a failed node" : "no session recorded", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_invalid_run_id_is_a_bad_request_and_a_caller_without_a_token_is_refused()
    {
        var (factory, client, _) = await HostAsync();
        await using var _ = factory;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/loopruns/nope/failed-node/retry", new { }, Ct)).StatusCode);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/v1/loopruns/{Guid.NewGuid()}/failed-node/retry", new { }, Ct)).StatusCode);
    }
}
