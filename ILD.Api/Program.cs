using ILD.Data;
using ILD.Data.Entities;
using ILD.Api.Configuration;
using ILD.Api.Middleware;
using ILD.Api.Hubs;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;

var loggingLevelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new JsonFormatter())
    .Enrich.FromLogContext()
    .MinimumLevel.ControlledBy(loggingLevelSwitch)
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting ILD API");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    var dataPath = Environment.GetEnvironmentVariable("ILD_DATA_PATH")
        ?? builder.Configuration["Storage:DataRoot"]
        ?? "data";
    var worktreesSubdir = builder.Configuration["Storage:WorktreesSubdir"] ?? "worktrees";
    var dbFile = builder.Configuration["Storage:DatabaseFile"] ?? "ild.db";
    var worktreesPath = Environment.GetEnvironmentVariable("ILD_WORKTREES_PATH")
        ?? Path.Combine(dataPath, worktreesSubdir);

    builder.Configuration["App:DataPath"] = dataPath;
    builder.Configuration["App:WorktreesPath"] = worktreesPath;
    builder.Configuration["App:DatabaseFile"] = dbFile;

    if (!Directory.Exists(dataPath))
        Directory.CreateDirectory(dataPath);

    if (!Directory.Exists(worktreesPath))
        Directory.CreateDirectory(worktreesPath);

    builder.Services.AddDataLayer(options =>
    {
        var dbPath = Path.Combine(dataPath, dbFile);
        options.UseSqlite($"Data Source={dbPath}");
    });

    builder.Services.AddIldServices();

    builder.Services.AddSingleton<LoggingLevelSwitch>(loggingLevelSwitch);

    builder.Services.AddControllers()
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
            o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        });

    builder.Services.AddSignalR();

    builder.Services.AddCors(options =>
    {
        var allowedOrigins = ILD.Api.Configuration.CorsConfiguration.ParseAllowedOrigins(
            Environment.GetEnvironmentVariable("ILD_ALLOWED_ORIGINS"));
        options.AddPolicy("AllowFrontend", policy =>
            policy.WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials());
    });

    builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
    });

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        dbContext.Database.Migrate();
        Log.Information("Database migrated at {DataPath}", Path.Combine(dataPath, dbFile));

        var templateStore = scope.ServiceProvider.GetRequiredService<ILD.Data.Stores.Interfaces.ILoopTemplateStore>();
        var mgr = scope.ServiceProvider.GetRequiredService<ILD.Core.Services.Interfaces.ILoopTemplateManager>();
        await ILD.Api.Configuration.TemplateSeeder.SeedAsync(templateStore, mgr);

        // Best-effort recovery: any LoopRun left in Running across restart
        var recovery = scope.ServiceProvider.GetRequiredService<ILD.Core.Services.Interfaces.IRecoveryManager>();
        foreach (var runId in await recovery.GetRecoverableRunIdsAsync())
        {
            try { await recovery.RecoverRunAsync(runId); }
            catch (Exception ex) { Log.Warning(ex, "Recovery failed for run {RunId}", runId); }
        }
    }

    app.UseSerilogRequestLogging();
    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
    }
    app.UseSecurityHeaders();
    app.UseCors("AllowFrontend");
    app.UseMiddleware<AuthMiddleware>();
    app.UseRouting();

    app.MapControllers();
    app.MapHub<LoopRunHub>("/hubs/loop-run");
    app.MapHub<WorkItemHub>("/hubs/work-item");

    var spaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "frontend", "dist");
    if (Directory.Exists(spaPath))
    {
        app.UseStaticFiles();
        app.MapFallbackToFile("index.html");
    }

    Log.Information("ILD API started");
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "ILD API terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// Required for WebApplicationFactory<Program> in integration tests.
public partial class Program;
