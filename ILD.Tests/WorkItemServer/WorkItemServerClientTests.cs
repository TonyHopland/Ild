using System.Net;
using ILD.Core.Services.Remote;
using ILD.WorkItemServer;
using ILD.WorkItemServer.Auth;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

    public ValueTask InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        Environment.SetEnvironmentVariable("WORKITEM_DB_CONNECTION_STRING", null);

        _factory = new WebApplicationFactory<WorkItemServerProgram>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WorkItemServer:ApiKeys"] = ApiKey,
                    ["Serilog:WriteToConsole"] = "false",
                });
            });
            b.ConfigureServices(services =>
            {
                services.RemoveHostedService<ILD.WorkItemServer.Hosting.StaleWorkItemReclaimer>();
                services.PostConfigure<ApiKeyOptions>(options => options.Keys = ApiKey);
                var existing = services.FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<WorkItemServerDbContext>));
                if (existing != null) services.Remove(existing);
                services.AddDbContext<WorkItemServerDbContext>(o =>
                {
                    o.UseSqlite(_conn);
                    o.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
                });
            });
        });

        var http = _factory.CreateClient();
        _client = new WorkItemServerClient(http);
        _opts = new WorkItemServerOptions { BaseUrl = "http://localhost", ApiKey = ApiKey };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
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
        }, TestContext.Current.CancellationToken);

        Assert.Equal("client-roundtrip", created.Title);
        Assert.Equal(new[] { "alpha", "beta" }, created.Tags);

        var fetched = await _client.GetAsync(_opts, created.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(RemoteWorkItemStatus.Backlog, fetched!.Status);
    }

    [Fact]
    public async Task Custom_branch_name_round_trips_through_create_get_and_update()
    {
        var created = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "custom-branch",
            BranchNameOverride = "feature/foo",
        }, TestContext.Current.CancellationToken);
        Assert.Equal("feature/foo", created.BranchNameOverride);
        Assert.Equal("feature/foo", (await _client.GetAsync(_opts, created.Id, TestContext.Current.CancellationToken))!.BranchNameOverride);

        var renamed = await _client.UpdateAsync(_opts, created.Id, new RemoteUpdateWorkItemRequest
        {
            BranchNameOverride = "feature/bar",
        }, TestContext.Current.CancellationToken);
        Assert.Equal("feature/bar", renamed!.BranchNameOverride);

        var cleared = await _client.UpdateAsync(_opts, created.Id, new RemoteUpdateWorkItemRequest
        {
            BranchNameOverride = "",
        }, TestContext.Current.CancellationToken);
        Assert.Null(cleared!.BranchNameOverride);
    }

    [Fact]
    public async Task Base_branch_round_trips_through_create_get_and_update()
    {
        var created = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "base-branch",
            BaseBranchOverride = "release/1.0",
        }, TestContext.Current.CancellationToken);
        Assert.Equal("release/1.0", created.BaseBranchOverride);
        Assert.Equal("release/1.0", (await _client.GetAsync(_opts, created.Id, TestContext.Current.CancellationToken))!.BaseBranchOverride);

        var moved = await _client.UpdateAsync(_opts, created.Id, new RemoteUpdateWorkItemRequest
        {
            BaseBranchOverride = "release/2.0",
        }, TestContext.Current.CancellationToken);
        Assert.Equal("release/2.0", moved!.BaseBranchOverride);

        var cleared = await _client.UpdateAsync(_opts, created.Id, new RemoteUpdateWorkItemRequest
        {
            BaseBranchOverride = "",
        }, TestContext.Current.CancellationToken);
        Assert.Null(cleared!.BaseBranchOverride);
    }

    [Fact]
    public async Task Get_returns_null_on_not_found()
    {
        var item = await _client.GetAsync(_opts, Guid.NewGuid().ToString(), TestContext.Current.CancellationToken);
        Assert.Null(item);
    }

    [Fact]
    public async Task Transition_to_running_succeeds_when_ready()
    {
        var created = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "tx", ForceStatus = RemoteWorkItemStatus.Ready,
        }, TestContext.Current.CancellationToken);
        var resp = await _client.TransitionAsync(_opts, created.Id, new RemoteTransitionRequest
        {
            TargetStatus = RemoteWorkItemStatus.Running,
        }, TestContext.Current.CancellationToken);
        Assert.True(resp.Success);
        Assert.Equal(RemoteWorkItemStatus.Running, resp.ActualStatus);
    }

    [Fact]
    public async Task Poll_returns_ready_items_and_heartbeats_active()
    {
        var ready = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "ready", ForceStatus = RemoteWorkItemStatus.Ready,
        }, TestContext.Current.CancellationToken);
        var running = await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest
        {
            Title = "running", ForceStatus = RemoteWorkItemStatus.Ready,
        }, TestContext.Current.CancellationToken);
        await _client.TransitionAsync(_opts, running.Id,
            new RemoteTransitionRequest { TargetStatus = RemoteWorkItemStatus.Running }, TestContext.Current.CancellationToken);

        var poll = await _client.PollAsync(_opts, new[] { running.Id }, TestContext.Current.CancellationToken);
        Assert.Contains(ready.Id, poll.ReadyItems.Select(x => x.Id));
        Assert.Contains(running.Id, poll.ActiveItems.Select(x => x.Id));
    }

    [Fact]
    public async Task List_filters_by_status()
    {
        await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest { Title = "a" }, TestContext.Current.CancellationToken);
        await _client.CreateAsync(_opts, new RemoteCreateWorkItemRequest { Title = "b", ForceStatus = RemoteWorkItemStatus.Ready }, TestContext.Current.CancellationToken);

        var ready = await _client.ListAsync(_opts, RemoteWorkItemStatus.Ready, null, TestContext.Current.CancellationToken);
        Assert.All(ready, w => Assert.Equal(RemoteWorkItemStatus.Ready, w.Status));
    }

    [Fact]
    public async Task A_refused_key_names_the_server_that_refused_it_and_the_setting_to_look_at()
    {
        // Callers turn this exception into "WorkItemServer unreachable", so the
        // message is the only thing distinguishing a key mismatch from a server
        // that is down — and, where two are running (a preview of ILD runs its
        // own next to the host's), which of them answered.
        var wrongKey = new WorkItemServerOptions { BaseUrl = _opts.BaseUrl, ApiKey = "not-the-configured-key" };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => _client.ListAsync(wrongKey, null, null, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains($"{_opts.BaseUrl}/workitems", ex.Message);
        Assert.Contains("WORKITEM_API_KEYS", ex.Message);
    }
}
