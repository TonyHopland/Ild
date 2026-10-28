using System.Net.Http.Headers;
using ILD.WorkItemServer;
using ILD.WorkItemServer.Auth;
using ILD.WorkItemServer.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// A WorkItem server on its own in-memory SQLite database with the API key it was
/// given. Both are supplied per instance rather than read from the process
/// environment, so hosts running side by side do not share them. The database
/// connection-string variable is cleared for the whole process, which every host
/// here wants, so none of them reaches for PostgreSQL. The stale item reclaimer is
/// off, so nothing moves a test's items behind its back.
/// </summary>
public class WorkItemServerFactory : WebApplicationFactory<WorkItemServerProgram>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _apiKey;
    private readonly IInterceptor[] _interceptors;

    /// <param name="interceptors">Added to every context on the server's database.</param>
    public WorkItemServerFactory(string apiKey, params IInterceptor[] interceptors)
    {
        _apiKey = apiKey;
        _interceptors = interceptors;
        _connection.Open();
        Environment.SetEnvironmentVariable("WORKITEM_DB_CONNECTION_STRING", null);
    }

    protected sealed override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkItemServer:ApiKeys"] = _apiKey,
            ["Serilog:WriteToConsole"] = "false",
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveHostedService<StaleWorkItemReclaimer>();
            services.PostConfigure<ApiKeyOptions>(options => options.Keys = _apiKey);
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<WorkItemServerDbContext>));
            if (descriptor != null) services.Remove(descriptor);
            services.AddDbContext<WorkItemServerDbContext>(opt => UseDatabase(opt));
            ConfigureServices(services);
        });
    }

    /// <summary>What a derived host replaces or adds on top of the server's own services.</summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    /// <summary>A client that sends the API key.</summary>
    public HttpClient KeyedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return client;
    }

    /// <summary>A client that sends the API key and names a human as the actor of every mutation.</summary>
    public HttpClient AuthedClient()
    {
        var client = KeyedClient();
        client.DefaultRequestHeaders.Add(ILD.Core.Services.Remote.WorkItemActor.HeaderName, TestActors.Human.ToHeaderValue());
        return client;
    }

    /// <summary>A context on the same database, for reading and writing rows the API does not expose.</summary>
    public WorkItemServerDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<WorkItemServerDbContext>();
        UseDatabase(options);
        return new WorkItemServerDbContext(options.Options) { HistoryActor = TestActors.ServerHuman };
    }

    private void UseDatabase(DbContextOptionsBuilder opt)
    {
        opt.UseSqlite(_connection);
        opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        opt.AddInterceptors(_interceptors);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}
