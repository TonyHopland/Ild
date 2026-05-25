using ILD.Core.Services.Remote;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net.Http.Json;

namespace ILD.Tests.Integration;

/// <summary>
/// Per-instance <see cref="WebApplicationFactory{TEntryPoint}"/> that boots the
/// real ILD API pipeline against an isolated in-memory SQLite database and a
/// per-instance temporary data directory. Each test should `new` one of these
/// (or use it as an <c>IClassFixture</c>) so the database, file system, and
/// singleton service instances cannot leak between tests.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection;
    private readonly FakeWorkItemServerHarness _serverHarness = new();
    private readonly string _dataRoot;

    public string AdminPassword { get; } = "ild-int-tests-admin-pw";

    public ApiFactory()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dataRoot = Path.Combine(Path.GetTempPath(), "ild-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        // Tests may run in parallel, so the env var must be a constant: every factory
        // that boots the API expects the same admin password to bootstrap login.
        Environment.SetEnvironmentVariable("ILD_PASSWORD", AdminPassword);
        Environment.SetEnvironmentVariable("ILD_DATA_PATH", null);
        Environment.SetEnvironmentVariable("ILD_WORKTREES_PATH", null);
        Environment.SetEnvironmentVariable("ILD_DB_CONNECTION_STRING", null);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataRoot"] = _dataRoot,
                ["Storage:WorktreesSubdir"] = "worktrees",
                ["Serilog:WriteToConsole"] = "false",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.AddDataStores();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection,
                sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly)));

            services.RemoveHostedService<ILD.Core.Services.Remote.RemoteWorkItemStartupReconciler>();
            services.RemoveHostedService<ILD.Core.Services.Remote.WorkItemScheduler>();
            services.GuardExternalServices();
            services.ReplaceSingleton<IAgentAdapterRegistry>(new FixedAgentAdapterRegistry());

            services.RemoveAll<IWorkItemServerClient>();
            services.RemoveAll<IWorkItemServerOptionsResolver>();
            services.AddSingleton<IWorkItemServerClient>(_serverHarness.Client);
            services.AddSingleton<IWorkItemServerOptionsResolver>(_serverHarness.Options);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connection.Dispose();
            _serverHarness.Dispose();
            try { Directory.Delete(_dataRoot, recursive: true); } catch { }
        }
        base.Dispose(disposing);
    }

    /// <summary>Logs in as the seeded admin user and returns an HttpClient with the bearer token attached.</summary>
    public async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "admin", password = AdminPassword });
        login.EnsureSuccessStatusCode();
        var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var body = await login.Content.ReadFromJsonAsync<LoginBody>(jsonOptions);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", body!.Token);
        return client;
    }

    private sealed record LoginBody(string Token, string Username);

    private sealed class FixedAgentAdapterRegistry : IAgentAdapterRegistry
    {
        public Func<IAgentAdapter> ResolveForProvider(AiProvider provider)
            => throw new InvalidOperationException("Integration tests using ApiFactory should not execute AI adapters.");

        public string[] GetAllSupportedProviderTypes()
            => ["opencode", "pi", "claude-code"];
    }
}
