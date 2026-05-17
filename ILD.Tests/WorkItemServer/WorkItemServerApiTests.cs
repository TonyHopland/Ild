using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ILD.WorkItemServer;
using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;


namespace ILD.Tests.WorkItemServer;

/// <summary>
/// End-to-end tests of the REST surface using <see cref="WebApplicationFactory{T}"/>.
/// Uses an in-memory SQLite shared via a kept-open connection so EF migrations
/// land on a database that survives the request scope.
/// </summary>
public sealed class WorkItemServerApiTests : IClassFixture<WorkItemServerApiTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<WorkItemServerProgram>
    {
        public const string ApiKey = "test-key";
        private readonly SqliteConnection _conn;

        public Factory()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("WORKITEM_API_KEYS", ApiKey);
            Environment.SetEnvironmentVariable("WORKITEM_DB_CONNECTION_STRING", null);
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WorkItemServer:ApiKeys"] = ApiKey,
                    ["Serilog:WriteToConsole"] = "false",
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveHostedService<ILD.WorkItemServer.Hosting.StaleWorkItemReclaimer>();
                var dbDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<WorkItemServerDbContext>));
                if (dbDescriptor != null) services.Remove(dbDescriptor);
                services.AddDbContext<WorkItemServerDbContext>(opt =>
                {
                    opt.UseSqlite(_conn);
                    opt.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _conn.Dispose();
        }
    }

    private readonly Factory _factory;
    public WorkItemServerApiTests(Factory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Factory.ApiKey);
        return c;
    }

    [Fact]
    public async Task Health_endpoint_is_reachable_without_auth()
    {
        var c = _factory.CreateClient();
        var resp = await c.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_requests_return_401()
    {
        var c = _factory.CreateClient();
        var resp = await c.GetAsync("/workitems");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Bad_token_returns_401()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        var resp = await c.GetAsync("/workitems");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Create_then_get_round_trips_through_the_api()
    {
        var c = AuthedClient();
        var create = await c.PostAsJsonAsync("/workitems", new CreateWorkItemRequest
        {
            Title = "round trip",
            Tags = new[] { "feature" },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var dto = await create.Content.ReadFromJsonAsync<WorkItemDto>();
        Assert.NotNull(dto);

        var fetched = await c.GetFromJsonAsync<WorkItemDto>($"/workitems/{dto!.Id}");
        Assert.Equal("round trip", fetched!.Title);
        Assert.Equal("feature", Assert.Single(fetched.Tags));
    }

    [Fact]
    public async Task Transition_to_Running_via_api_returns_success_response()
    {
        var c = AuthedClient();
        var create = await c.PostAsJsonAsync("/workitems", new CreateWorkItemRequest { Title = "x" });
        var dto = (await create.Content.ReadFromJsonAsync<WorkItemDto>())!;

        var resp = await c.PostAsJsonAsync($"/workitems/{dto.Id}/transition", new TransitionRequest
        {
            TargetStatus = WorkItemStatus.Running,
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = (await resp.Content.ReadFromJsonAsync<TransitionResponse>())!;
        Assert.True(body.Success);
        Assert.Equal(WorkItemStatus.Running, body.ActualStatus);
    }

    [Fact]
    public async Task Poll_endpoint_returns_active_and_ready_lists()
    {
        var c = AuthedClient();
        var create = await c.PostAsJsonAsync("/workitems", new CreateWorkItemRequest
        {
            Title = "ready",
            ForceStatus = WorkItemStatus.Ready,
        });
        var dto = (await create.Content.ReadFromJsonAsync<WorkItemDto>())!;

        var resp = await c.GetFromJsonAsync<PollResponse>("/workitems/poll");
        Assert.NotNull(resp);
        Assert.Contains(resp!.ReadyItems, r => r.Id == dto.Id);
    }
}
