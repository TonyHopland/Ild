using ILD.WorkItemServer.Auth;
using ILD.WorkItemServer.Hosting;
using ILD.WorkItemServer.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
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

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console(new JsonFormatter())
            .Enrich.FromLogContext()
            .CreateLogger();
        builder.Host.UseSerilog();

        var dataPath = Environment.GetEnvironmentVariable("WORKITEM_DATA_PATH")
            ?? builder.Configuration["WorkItemServer:DataPath"]
            ?? "data";
        if (!Directory.Exists(dataPath))
            Directory.CreateDirectory(dataPath);
        var dbFile = builder.Configuration["WorkItemServer:DatabaseFile"] ?? "workitems.db";
        var dbPath = Path.Combine(dataPath, dbFile);

        builder.Services.AddDbContext<WorkItemServerDbContext>(opt =>
            opt.UseSqlite($"Data Source={dbPath}"));

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

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkItemServerDbContext>();
            db.Database.Migrate();
        }

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.UseMiddleware<ApiKeyMiddleware>();
        app.MapControllers();

        return app;
    }
}
