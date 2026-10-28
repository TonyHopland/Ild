using System.Net;
using System.Net.Http.Headers;
using ILD.WorkItemServer;
using ILD.WorkItemServer.Auth;
using ILD.WorkItemServer.Backup;
using ILD.WorkItemServer.Domain;
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
/// The WorkItem Server's non-contract backup endpoints (/maintenance/backup): the
/// API key requirement, an import that is not capped at Kestrel's default body
/// size and the heartbeat refresh after an import.
///
/// <para>The database here is SQLite, so the server's PostgreSQL archive is
/// replaced by a recording stand-in registered as <c>IDatabaseArchive</c>. The
/// endpoints, their gate and the reclaimer are expected to work whenever such an
/// archive is registered; production registers it only when a connection string
/// is set.</para>
/// </summary>
public sealed class BackupEndpointsTests
{
    private const string ApiKey = "backup-endpoints-key";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Records what is exported and drains and counts what is imported.</summary>
    public sealed class RecordingArchive : IDatabaseArchive
    {
        public long ImportedBytes;
        public int ImportCalls;
        public int ExportCalls;
        public Task? HoldImport;
        public TaskCompletionSource ImportEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ArchiveInfo> DescribeAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ArchiveInfo("0.0.0", [], DatabaseBytes: null));

        public Task ExportAsync(Stream output, string? keyCheck, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ExportCalls);
            return Task.CompletedTask;
        }

        public async Task ImportAsync(Stream input, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ImportCalls);
            ImportEntered.TrySetResult();
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0) Interlocked.Add(ref ImportedBytes, read);
            if (HoldImport is not null) await HoldImport;
        }
    }

    private sealed class Server : WebApplicationFactory<WorkItemServerProgram>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        public RecordingArchive Archive { get; } = new();

        public Server()
        {
            _connection.Open();
            Environment.SetEnvironmentVariable("WORKITEM_DB_CONNECTION_STRING", null);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkItemServer:ApiKeys"] = ApiKey,
                ["Serilog:WriteToConsole"] = "false",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveHostedService<StaleWorkItemReclaimer>();
                services.PostConfigure<ApiKeyOptions>(options => options.Keys = ApiKey);
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<WorkItemServerDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<WorkItemServerDbContext>(opt =>
                {
                    opt.UseSqlite(_connection);
                    opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
                });
                services.AddSingleton<IDatabaseArchive>(Archive);
            });
        }

        /// <summary>Marks every migration applied, so the import's forward migration finds nothing to do on this SQLite copy.</summary>
        public void MarkMigrationsApplied()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkItemServerDbContext>();
            db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)");
            foreach (var id in db.Database.GetMigrations())
                db.Database.ExecuteSqlRaw(
                    "INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({0}, {1})", id, "10.0.0");
        }

        public HttpClient Client(bool withKey = true)
        {
            var client = CreateClient();
            if (withKey)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
                client.DefaultRequestHeaders.Add(ILD.Core.Services.Remote.WorkItemActor.HeaderName, TestActors.Human.ToHeaderValue());
            }
            return client;
        }

        public string Seed(WorkItemStatus status, DateTime? heartbeat)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkItemServerDbContext>();
            var item = new WorkItem
            {
                Id = "wi-" + Guid.NewGuid().ToString("N")[..8],
                Title = "t",
                Status = status,
                LastHeartbeatAt = heartbeat,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.WorkItems.Add(item);
            db.SaveChanges();
            return item.Id;
        }

        public (WorkItemStatus Status, DateTime? Heartbeat) Read(string id)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkItemServerDbContext>();
            var item = db.WorkItems.AsNoTracking().Single(w => w.Id == id);
            return (item.Status, item.LastHeartbeatAt);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _connection.Dispose();
        }
    }

    private static HttpContent Body(int bytes = 16)
        => new ByteArrayContent(new byte[bytes]) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } };

    [Fact]
    public async Task The_backup_endpoints_require_the_API_key()
    {
        using var server = new Server();
        using var anonymous = server.Client(withKey: false);
        using var wrongKey = server.Client(withKey: false);
        wrongKey.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");

        foreach (var client in new[] { anonymous, wrongKey })
        {
            using var info = await client.GetAsync("/maintenance/backup", Ct);
            using var export = await client.PostAsync("/maintenance/backup/export", new StringContent(""), Ct);
            using var import = await client.PostAsync("/maintenance/backup/import", Body(), Ct);

            Assert.Equal(HttpStatusCode.Unauthorized, info.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, export.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, import.StatusCode);
        }
        Assert.Equal(0, server.Archive.ExportCalls);
        Assert.Equal(0, server.Archive.ImportCalls);
    }

    [Fact]
    public void The_import_endpoint_lifts_the_request_body_size_limit()
    {
        using var server = new Server();

        var endpoint = server.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText!.EndsWith("/import", StringComparison.OrdinalIgnoreCase)
                && e.RoutePattern.RawText.Contains("maintenance", StringComparison.OrdinalIgnoreCase));

        // The test server has no Kestrel limit to hit, so the property is read where it is declared:
        // a null limit on the endpoint is what lifts Kestrel's ~30 MB default for this request.
        var limit = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>();
        Assert.NotNull(limit);
        Assert.Null(limit!.MaxRequestBodySize);
    }

    [Fact]
    public async Task An_import_hands_the_whole_body_to_the_archive_and_refreshes_the_heartbeat_of_claimed_items()
    {
        using var server = new Server();
        server.MarkMigrationsApplied();
        var old = DateTime.UtcNow.AddDays(-3);
        var running = server.Seed(WorkItemStatus.Running, old);
        var waiting = server.Seed(WorkItemStatus.WaitingForIld, old);
        var runningNeverSeen = server.Seed(WorkItemStatus.Running, null);
        var ready = server.Seed(WorkItemStatus.Ready, old);
        var done = server.Seed(WorkItemStatus.Done, old);
        using var client = server.Client();
        var before = DateTime.UtcNow;

        using var response = await client.PostAsync("/maintenance/backup/import", Body(300_000), Ct);
        var after = DateTime.UtcNow;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(300_000, server.Archive.ImportedBytes);
        foreach (var id in new[] { running, waiting })
        {
            var heartbeat = server.Read(id).Heartbeat;
            Assert.NotNull(heartbeat);
            Assert.InRange(heartbeat!.Value, before.AddSeconds(-1), after.AddSeconds(1));
        }
        Assert.Null(server.Read(runningNeverSeen).Heartbeat);
        Assert.Equal(old, server.Read(ready).Heartbeat);
        Assert.Equal(old, server.Read(done).Heartbeat);
        Assert.Equal(WorkItemStatus.Running, server.Read(running).Status);
        Assert.Equal(WorkItemStatus.WaitingForIld, server.Read(waiting).Status);
    }
}
