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
    private readonly IReadOnlyDictionary<string, string?> _extraConfiguration;

    public string AdminUsername { get; } = "admin";
    public string AdminPassword { get; } = "ild-int-tests-admin-pw";

    /// <param name="extraConfiguration">
    /// Configuration entries layered over the defaults, for host-level settings the
    /// API reads straight from configuration rather than from the database — e.g.
    /// <c>ILD_PREVIEW_PROXY_BASE</c>. Supplied here rather than as environment
    /// variables so parallel factories cannot see each other's values.
    /// </param>
    public ApiFactory(IReadOnlyDictionary<string, string?>? extraConfiguration = null)
    {
        _extraConfiguration = extraConfiguration ?? new Dictionary<string, string?>();
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dataRoot = Path.Combine(Path.GetTempPath(), "ild-int-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        // Tests may run in parallel, so the env var must be a constant: every factory
        // that boots the API expects the same admin credentials to bootstrap login.
        // Both halves have to be pinned, not just the password: AuthService takes the
        // bootstrap username from ILD_USERNAME, so an ambient value — a preview shell
        // exports one — seeds a user LoginAsync's hardcoded "admin" cannot log in as,
        // and every test that needs a token fails at the handshake.
        Environment.SetEnvironmentVariable("ILD_USERNAME", AdminUsername);
        Environment.SetEnvironmentVariable("ILD_PASSWORD", AdminPassword);
        Environment.SetEnvironmentVariable("ILD_DATA_PATH", null);
        Environment.SetEnvironmentVariable("ILD_WORKTREES_PATH", null);
        Environment.SetEnvironmentVariable("ILD_DB_CONNECTION_STRING", null);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // The API decides whether to serve the SPA by looking for a wwwroot next to
        // the entry assembly, but static files and the fallback are then served from
        // the web root, which WebApplicationFactory otherwise resolves against the
        // ILD.Api project directory. In the container both are /app; pointing the web
        // root at the test output makes them agree here too, so the SPA branch of the
        // pipeline is exercised as shipped rather than registered and then 404ing.
        builder.UseWebRoot(Path.Combine(AppContext.BaseDirectory, "wwwroot"));

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataRoot"] = _dataRoot,
                ["Storage:WorktreesSubdir"] = "worktrees",
                ["Serilog:WriteToConsole"] = "false",
            });
            config.AddInMemoryCollection(_extraConfiguration);
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
        // Tear the host down first so its service provider, hosted services and
        // any in-flight request scopes (all holding AppDbContexts bound to the
        // shared, single-threaded SQLite connection) are gone before we dispose
        // the connection. Disposing _connection while the host is still alive
        // races SqliteConnection.Dispose against those consumers and throws
        // intermittently under parallel test load.
        base.Dispose(disposing);
        if (disposing)
        {
            // Even with the host torn down first, a hosted service that is still
            // flushing on a background thread can touch the shared connection as
            // it is disposed, so SqliteConnection.Dispose still throws
            // intermittently under parallel test load. The test's assertions have
            // already run by this point, so a teardown-only race must not fail the
            // test — swallow it like the temp-directory cleanup below.
            try { _connection.Dispose(); } catch { }
            _serverHarness.Dispose();
            try { Directory.Delete(_dataRoot, recursive: true); } catch { }
        }
    }

    /// <summary>Logs in as the seeded admin user and returns an HttpClient with the bearer token attached.</summary>
    public async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await LoginAsync(client));
        return client;
    }

    /// <summary>
    /// A session token for the seeded admin, for callers that must place it
    /// somewhere other than the Authorization header — WebSocket and SignalR
    /// handshakes carry it as an <c>access_token</c> query parameter.
    /// </summary>
    public async Task<string> GetAdminTokenAsync() => await LoginAsync(CreateClient());

    private async Task<string> LoginAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = AdminUsername, password = AdminPassword });
        login.EnsureSuccessStatusCode();
        var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var body = await login.Content.ReadFromJsonAsync<LoginBody>(jsonOptions);
        return body!.Token;
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
