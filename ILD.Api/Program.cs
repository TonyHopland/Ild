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

var initialLogLevel = Enum.TryParse<LogEventLevel>(
    Environment.GetEnvironmentVariable("ILD_LOG_LEVEL"), ignoreCase: true, out var parsedLevel)
        ? parsedLevel
        : LogEventLevel.Information;
var loggingLevelSwitch = new LoggingLevelSwitch(initialLogLevel);

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, _, loggerConfiguration) =>
    {
        loggerConfiguration
            .Enrich.FromLogContext()
            .MinimumLevel.ControlledBy(loggingLevelSwitch);

        if (context.Configuration.GetValue("Serilog:WriteToConsole", true))
        {
            loggerConfiguration.WriteTo.Console(new JsonFormatter());
        }
    });

    var dataPath = Environment.GetEnvironmentVariable("ILD_DATA_PATH")
        ?? builder.Configuration["Storage:DataRoot"]
        ?? "data";
    var worktreesSubdir = builder.Configuration["Storage:WorktreesSubdir"] ?? "worktrees";
    var worktreesPath = Environment.GetEnvironmentVariable("ILD_WORKTREES_PATH")
        ?? Path.Combine(dataPath, worktreesSubdir);

    builder.Configuration["App:DataPath"] = dataPath;
    builder.Configuration["App:WorktreesPath"] = worktreesPath;

    if (!Directory.Exists(dataPath))
        Directory.CreateDirectory(dataPath);

    if (!Directory.Exists(worktreesPath))
        Directory.CreateDirectory(worktreesPath);

    var connectionString = Environment.GetEnvironmentVariable("ILD_DB_CONNECTION_STRING")
        ?? builder.Configuration["ILD_DB_CONNECTION_STRING"];

    if (connectionString != null && connectionString.Length > 0)
    {
        builder.Services.AddDataLayer(options =>
        {
            options.UseNpgsql(connectionString, npg => npg.MigrationsHistoryTable("__EFMigrationsHistory", "public"));
        });
    }
    // When no connection string is set (e.g. integration tests), skip AddDataLayer
    // so no Npgsql internal services are registered. The test factory substitutes
    // its own DbContext + data stores in ConfigureServices.

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

    _ = app.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>();

    using (var scope = app.Services.CreateScope())
    {
        var dbContext = scope.ServiceProvider.GetService<AppDbContext>();
        if (dbContext != null)
        {
            if (connectionString != null && connectionString.Length > 0)
            {
                dbContext.Database.Migrate();
            }
            else
            {
                dbContext.Database.EnsureCreated();
            }
            Log.Information("Database ready");

            // Retire the obsolete AI rejectPattern config on already-seeded
            // databases (the seeder is insert-only, so existing rows are never
            // rewritten). Idempotent: a no-op once every node is migrated.
            var rejectMigrated = await ILD.Data.Migrations.AiRejectPatternMigrator.MigrateAsync(dbContext);
            if (rejectMigrated > 0)
                Log.Information("Migrated {Count} AI node(s) from rejectPattern to named custom edges", rejectMigrated);

            if (ILD.Data.Security.SecretProtector.IsEnabled)
                Log.Information("Secret encryption-at-rest is enabled (ILD_SECRET_KEY set)");
            else
                Log.Warning("ILD_SECRET_KEY is not set — provider API keys and webhook secrets are stored in plaintext. Set it to enable encryption-at-rest.");

            var templateStore = scope.ServiceProvider.GetRequiredService<ILD.Data.Stores.Interfaces.ILoopTemplateStore>();
            var mgr = scope.ServiceProvider.GetRequiredService<ILD.Core.Services.Interfaces.ILoopTemplateManager>();
            await ILD.Api.Configuration.TemplateSeeder.SeedAsync(templateStore, mgr);

            var settingStore = scope.ServiceProvider.GetRequiredService<ILD.Data.Stores.Interfaces.IAppSettingStore>();
            await ILD.Api.Configuration.TemplateSeeder.SeedWorkItemServerAsync(settingStore);

            // When the remote work-item scheduler is enabled, startup recovery
            // belongs to RemoteWorkItemStartupReconciler: it consults the
            // server before resuming, so a run whose work item the server has
            // since reclaimed (stale heartbeat) is cancelled instead of blindly
            // resumed into a duplicate of the freshly claimed run.
            var schedulerOpts = scope.ServiceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ILD.Core.Services.Remote.WorkItemSchedulerOptions>>()
                .CurrentValue;
            if (!schedulerOpts.Enabled || string.IsNullOrWhiteSpace(schedulerOpts.BaseUrl))
            {
                var recovery = scope.ServiceProvider.GetRequiredService<ILD.Core.Services.Interfaces.IRecoveryManager>();
                foreach (var runId in await recovery.GetRecoverableRunIdsAsync())
                {
                    try { await recovery.RecoverRunAsync(runId); }
                    catch (Exception ex) { Log.Warning(ex, "Recovery failed for run {RunId}", runId); }
                }
            }
        }
    }

    app.UseSerilogRequestLogging();
    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
    }
    app.UseSecurityHeaders();
    app.UseCors("AllowFrontend");

    var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    if (Directory.Exists(wwwroot))
    {
        app.UseDefaultFiles();
        app.UseStaticFiles();
    }

    app.UseWebSockets();
    app.UseMiddleware<AuthMiddleware>();
    app.UseRouting();

    app.MapControllers();
    app.MapHub<LoopRunHub>("/hubs/loop-run");
    app.MapHub<WorkItemHub>("/hubs/work-item");
    app.MapHub<ChatHub>("/hubs/chat");

    if (Directory.Exists(wwwroot))
    {
        app.MapFallbackToFile("index.html");
    }

    await app.RunAsync();
}
finally
{
    Log.CloseAndFlush();
}

// Required for WebApplicationFactory<Program> in integration tests.
public partial class Program;
