using System.Reflection;
using ILD.WorkItemServer.Auth;
using ILD.WorkItemServer.Hosting;
using ILD.WorkItemServer.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace ILD.WorkItemServer;

public sealed class WorkItemServerProgram
{
    public static void Main(string[] args) => CreateApp(args).Run();

    /// <summary>
    /// Build the configured <see cref="WebApplication"/> without running it.
    /// Exposed so integration tests can stand the server up against an
    /// in-memory database via <c>WebApplicationFactory</c>.
    /// </summary>
    public static WebApplication CreateApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var logLevel = Enum.TryParse<LogEventLevel>(
            Environment.GetEnvironmentVariable("WORKITEM_LOG_LEVEL"), ignoreCase: true, out var parsedLevel)
                ? parsedLevel
                : LogEventLevel.Information;

        builder.Host.UseSerilog((context, _, loggerConfiguration) =>
        {
            loggerConfiguration
                .Enrich.FromLogContext()
                .MinimumLevel.Is(logLevel);

            if (context.Configuration.GetValue("Serilog:WriteToConsole", true))
            {
                loggerConfiguration.WriteTo.Console(new JsonFormatter());
            }
        });

        var dataPath = Environment.GetEnvironmentVariable("WORKITEM_DATA_PATH")
            ?? builder.Configuration["WorkItemServer:DataPath"]
            ?? "data";
        if (!Directory.Exists(dataPath))
            Directory.CreateDirectory(dataPath);

        var connectionString = Environment.GetEnvironmentVariable("WORKITEM_DB_CONNECTION_STRING")
            ?? builder.Configuration["WORKITEM_DB_CONNECTION_STRING"];

        if (connectionString != null && connectionString.Length > 0)
        {
            builder.Services.AddDbContext<WorkItemServerDbContext>(opt =>
                opt.UseNpgsql(connectionString, npg => npg.MigrationsHistoryTable("__EFMigrationsHistory", "public")));
        }
        // When no connection string is set (e.g. integration tests), skip registration
        // so no Npgsql internal services are registered. The test factory substitutes
        // its own DbContext in ConfigureServices.

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IWorkItemService, WorkItemService>();

        builder.Services.Configure<ApiKeyOptions>(opts =>
        {
            opts.Keys = Environment.GetEnvironmentVariable("WORKITEM_API_KEYS")
                ?? builder.Configuration["WorkItemServer:ApiKeys"]
                ?? string.Empty;
        });

        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOpenApi();
        builder.Services.AddHostedService<StaleWorkItemReclaimer>();
        builder.Services.AddHostedService<WorkQueueReconciler>();

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetService<WorkItemServerDbContext>();
            if (db != null)
            {
                if (connectionString != null && connectionString.Length > 0)
                {
                    db.Database.Migrate();
                }
                else
                {
                    db.Database.EnsureCreated();
                }
            }
        }

        // Surface the build-time informational version (CI stamps it to the
        // release tag via -p:Version; see docs/adr/0012) so the published
        // image's in-image version matches its tag.
        var version = typeof(WorkItemServerProgram).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        app.MapGet("/health", () => Results.Ok(new { status = "ok", version }));
        app.UseMiddleware<ApiKeyMiddleware>();
        app.MapControllers();

        return app;
    }
}
