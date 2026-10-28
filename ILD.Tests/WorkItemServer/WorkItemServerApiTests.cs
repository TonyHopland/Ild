using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using ILD.WorkItemServer;
using ILD.WorkItemServer.Dtos;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// End-to-end tests of the REST surface using <see cref="WebApplicationFactory{T}"/>.
/// Uses an in-memory SQLite shared via a kept-open connection so EF migrations
/// land on a database that survives the request scope.
/// </summary>
public sealed class WorkItemServerApiTests : IClassFixture<WorkItemServerApiTests.Factory>
{
    public sealed class Factory() : WorkItemServerFactory(ApiKey)
    {
        public const string ApiKey = "test-key";
    }

    private readonly Factory _factory;
    public WorkItemServerApiTests(Factory factory) => _factory = factory;

    private HttpClient AuthedClient() => _factory.AuthedClient();

    [Fact]
    public async Task Health_endpoint_reports_the_stamped_version()
    {
        var c = _factory.CreateClient();
        var body = await c.GetFromJsonAsync<HealthBody>("/health", TestContext.Current.CancellationToken);

        var expected = typeof(WorkItemServerProgram).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.NotNull(body);
        Assert.Equal("ok", body!.Status);
        // The image's in-image version is the build-time informational version
        // (release stamp == tag; docs/adr/0012), with no source-revision suffix.
        Assert.Equal(expected, body.Version);
        Assert.DoesNotContain("+", body.Version);
    }

    private sealed record HealthBody(string Status, string Version);

    [Fact]
    public async Task Unauthenticated_requests_return_401()
    {
        var c = _factory.CreateClient();
        var resp = await c.GetAsync("/workitems", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Bad_token_returns_401()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        var resp = await c.GetAsync("/workitems", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Pull_requests_round_trip_on_the_work_item()
    {
        var c = AuthedClient();
        var create = await c.PostAsJsonAsync("/workitems", new CreateWorkItemRequest { Title = "with a PR" }, cancellationToken: TestContext.Current.CancellationToken);
        var dto = (await create.Content.ReadFromJsonAsync<WorkItemDto>(TestContext.Current.CancellationToken))!;

        var recorded = await c.PostAsJsonAsync($"/workitems/{dto.Id}/pull-requests", new RecordPullRequestRequest
        {
            Url = "https://forgejo/repo/pulls/1",
            LoopRunId = Guid.NewGuid(),
            CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, recorded.StatusCode);

        var fetched = await c.GetFromJsonAsync<WorkItemDto>($"/workitems/{dto.Id}", TestContext.Current.CancellationToken);
        Assert.Equal("https://forgejo/repo/pulls/1", Assert.Single(fetched!.PullRequests).Url);
    }

    [Fact]
    public async Task Recording_a_PR_answers_a_missing_item_and_a_bad_report_differently()
    {
        var c = AuthedClient();
        var create = await c.PostAsJsonAsync("/workitems", new CreateWorkItemRequest { Title = "x" }, cancellationToken: TestContext.Current.CancellationToken);
        var dto = (await create.Content.ReadFromJsonAsync<WorkItemDto>(TestContext.Current.CancellationToken))!;

        var missing = await c.PostAsJsonAsync("/workitems/WI-nope/pull-requests",
            new RecordPullRequestRequest { Url = "https://forgejo/repo/pulls/1" }, cancellationToken: TestContext.Current.CancellationToken);
        var bad = await c.PostAsJsonAsync($"/workitems/{dto.Id}/pull-requests",
            new RecordPullRequestRequest { Url = "" }, cancellationToken: TestContext.Current.CancellationToken);

        // Only a genuinely absent work item may answer 404 — a client that is
        // told that stops asking, and the PR link it was reporting is lost.
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
