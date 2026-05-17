using FluentAssertions;
using ILD.Core.Services.Remote;
using ILD.WorkItemServer;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// Verifies the typed HTTP client lines up with the live server. Reuses the
/// same WebApplicationFactory pattern as the API tests so these never go
/// stale relative to the server contract.
/// </summary>
public sealed class WorkItemServerClientTests : IAsyncLifetime
{
    private const string ApiKey = "test-key";
    private SqliteConnection _conn = null!;
    private WebApplicationFactory<WorkItemServerProgram> _factory = null!;
    private WorkItemServerClient _client = null!;
    private WorkItemServerOptions _opts = null!;

    public Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        _factory = new WebApplicationFactory<WorkItemServerProgram>().WithWebHostBuilder(b =>
        {
            b.UseSetting("WorkItemServer:ApiKeys", ApiKey);
            b.ConfigureServices(services =>
            {
                var existing = services.FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<WorkItemServerDbContext>));
                if (existing != null) services.Remove(existing);
                services.AddDbContext<WorkItemServerDbContext>(o =>
                {
                    o.UseSqlite(_conn);
                    o.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
                });
                // Schema creation is handled by the server's own startup
                // (Database.Migrate()). Calling EnsureCreated() here would
                // create the tables first and then the migration step would
                // fail with "table already exists".
            });
        });

        var http = _factory.CreateClient();
        _client = new WorkItemServerClient(http);
        _opts = new WorkItemServerOptions { BaseUrl = "http://localhost", ApiKey = ApiKey };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        _conn.Dispose();
    }

    [Fact]
    public async Task Create_then_get_round_trips_via_client()
    {
        var created = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "client-roundtrip",
            Tags = new[] { "alpha", "beta" },
            Priority = RemoteWorkItemPriority.High,
        });

        created.Title.Should().Be("client-roundtrip");
        created.Tags.Should().BeEquivalentTo(new[] { "alpha", "beta" });

        var fetched = await _client.GetAsync(_opts, created.Id);
        fetched.Should().NotBeNull();
        fetched!.Status.Should().Be(RemoteWorkItemStatus.Backlog);
    }

    [Fact]
    public async Task Get_returns_null_on_not_found()
    {
        var item = await _client.GetAsync(_opts, Guid.NewGuid().ToString());
        item.Should().BeNull();
    }

    [Fact]
    public async Task Transition_to_running_succeeds_when_ready()
    {
        var created = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "tx", ForceStatus = RemoteWorkItemStatus.Ready,
        });
        var resp = await _client.TransitionAsync(_opts, created.Id, new RemoteTransitionRequest
        {
            TargetStatus = RemoteWorkItemStatus.Running,
        });
        resp.Success.Should().BeTrue();
        resp.ActualStatus.Should().Be(RemoteWorkItemStatus.Running);
    }

    [Fact]
    public async Task Poll_returns_ready_items_and_heartbeats_active()
    {
        var ready = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "ready", ForceStatus = RemoteWorkItemStatus.Ready,
        });
        var running = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "running", ForceStatus = RemoteWorkItemStatus.Ready,
        });
        await _client.TransitionAsync(_opts, running.Id,
            new RemoteTransitionRequest { TargetStatus = RemoteWorkItemStatus.Running });

        var poll = await _client.PollAsync(_opts, new[] { running.Id });
        poll.ReadyItems.Select(x => x.Id).Should().Contain(ready.Id);
        poll.ActiveItems.Select(x => x.Id).Should().Contain(running.Id);
    }

    [Fact]
    public async Task List_filters_by_status()
    {
        await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest { Title = "a" });
        await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest { Title = "b", ForceStatus = RemoteWorkItemStatus.Ready });

        var ready = await _client.ListAsync(_opts, RemoteWorkItemStatus.Ready, null);
        ready.Should().OnlyContain(w => w.Status == RemoteWorkItemStatus.Ready);
    }
}
