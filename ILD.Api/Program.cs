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

            var templateStore = scope.ServiceProvider.GetRequiredService<ILD.Data.Stores.Interfaces.ILoopTemplateStore>();
            var mgr = scope.ServiceProvider.GetRequiredService<ILD.Core.Services.Interfaces.ILoopTemplateManager>();
            await ILD.Api.Configuration.TemplateSeeder.SeedAsync(templateStore, mgr);

            var providerStore = scope.ServiceProvider.GetRequiredService<ILD.Data.Stores.Interfaces.IProviderStore>();
            await ILD.Api.Configuration.TemplateSeeder.SeedRemoteProviderAsync(providerStore);

            var recovery = scope.ServiceProvider.GetRequiredService<ILD.Core.Services.Interfaces.IRecoveryManager>();
            foreach (var runId in await recovery.GetRecoverableRunIdsAsync())
            {
                try { await recovery.RecoverRunAsync(runId); }
                catch (Exception ex) { Log.Warning(ex, "Recovery failed for run {RunId}", runId); }
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

    app.UseMiddleware<AuthMiddleware>();
    app.UseRouting();

    app.MapControllers();
    app.MapHub<LoopRunHub>("/hubs/loop-run");
    app.MapHub<WorkItemHub>("/hubs/work-item");

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
