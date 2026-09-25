using System.Net.Http.Headers;
using ILD.WorkItemServer;
using ILD.WorkItemServer.Attachments;
using ILD.WorkItemServer.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// A WorkItem server bound to its own in-memory SQLite database, with its
/// attachment limits and API key supplied per instance. Both replace what the
/// host read from the process environment, so hosts running side by side
/// cannot see each other's values.
/// </summary>
internal sealed class AttachmentServerFactory : WebApplicationFactory<WorkItemServerProgram>
{
    public const string ApiKey = "attachments-test-key";

    private readonly SqliteConnection _connection;
    private readonly AttachmentLimits _limits;

    /// <param name="maxAttachmentMb">Per file; null means the default.</param>
    /// <param name="maxTotalMb">Per work item; null means the default.</param>
    public AttachmentServerFactory(int? maxAttachmentMb = null, int? maxTotalMb = null)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _limits = AttachmentLimits.FromEnvironment(name => name switch
        {
            AttachmentLimits.MaxAttachmentMbVariable => maxAttachmentMb?.ToString(),
            AttachmentLimits.MaxAttachmentsTotalMbVariable => maxTotalMb?.ToString(),
            _ => null,
        });
        Environment.SetEnvironmentVariable("WORKITEM_DB_CONNECTION_STRING", null);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
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
            services.ReplaceSingleton(_limits);
            services.PostConfigure<ApiKeyOptions>(options => options.Keys = ApiKey);
            var dbDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<WorkItemServerDbContext>));
            if (dbDescriptor != null) services.Remove(dbDescriptor);
            services.AddDbContext<WorkItemServerDbContext>(opt => Configure(opt));
        });
    }

    private void Configure(DbContextOptionsBuilder opt)
    {
        opt.UseSqlite(_connection);
        opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    public HttpClient AuthedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        return client;
    }

    /// <summary>A context on the same database, for reading and writing rows the API does not expose.</summary>
    public WorkItemServerDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<WorkItemServerDbContext>();
        Configure(options);
        return new WorkItemServerDbContext(options.Options);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        _connection.Dispose();
    }
}
