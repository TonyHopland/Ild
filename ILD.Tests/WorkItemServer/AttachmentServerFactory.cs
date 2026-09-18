using System.Net.Http.Headers;
using ILD.WorkItemServer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// A WorkItem server bound to its own in-memory SQLite database, with the
/// attachment limits it will read at startup supplied per instance. The
/// variables are set in the constructor rather than in
/// <c>ConfigureWebHost</c> because the host reads them while it is being built,
/// which is before the factory's own configuration callbacks run.
/// </summary>
internal sealed class AttachmentServerFactory : WebApplicationFactory<WorkItemServerProgram>
{
    public const string ApiKey = "attachments-test-key";
    public const string MaxAttachmentMbVariable = "ILD_MAX_ATTACHMENT_MB";
    public const string MaxAttachmentsTotalMbVariable = "ILD_MAX_ATTACHMENTS_TOTAL_MB";

    private readonly SqliteConnection _connection;
    private readonly EnvironmentVariableScope _environment;

    public AttachmentServerFactory(int? maxAttachmentMb = null, int? maxTotalMb = null)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _environment = new EnvironmentVariableScope(
            ("WORKITEM_API_KEYS", ApiKey),
            ("WORKITEM_DB_CONNECTION_STRING", null),
            (MaxAttachmentMbVariable, maxAttachmentMb?.ToString()),
            (MaxAttachmentsTotalMbVariable, maxTotalMb?.ToString()));
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
        _environment.Dispose();
    }
}
