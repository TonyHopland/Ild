using ILD.Core.Models;
using ILD.Api.Configuration;
using ILD.Api.Middleware;
using ILD.Api.Hubs;
using Microsoft.EntityFrameworkCore;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .Enrich.FromLogContext()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting ILD API");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    var dataPath = Environment.GetEnvironmentVariable("ILD_DATA_PATH") ?? "data";
    var worktreesPath = Environment.GetEnvironmentVariable("ILD_WORKTREES_PATH") ?? Path.Combine(dataPath, "worktrees");

    builder.Configuration["App:DataPath"] = dataPath;
    builder.Configuration["App:WorktreesPath"] = worktreesPath;

    if (!Directory.Exists(dataPath))
        Directory.CreateDirectory(dataPath);

    if (!Directory.Exists(worktreesPath))
        Directory.CreateDirectory(worktreesPath);

    builder.Services.AddDbContext<AppDbContext>(options =>
    {
        var dbPath = Path.Combine(dataPath, "ild.db");
        options.UseSqlite($"Data Source={dbPath}");
    });

    builder.Services.AddIldServices();

    builder.Services.AddControllers()
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        });

    builder.Services.AddSignalR();

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowFrontend", policy =>
            policy.SetIsOriginAllowed(_ => true)
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
        dbContext.Database.EnsureCreated();
        Log.Information("Database ensured at {DataPath}", Path.Combine(dataPath, "ild.db"));

        var mgr = scope.ServiceProvider.GetRequiredService<ILD.Core.Services.Interfaces.ILoopTemplateManager>();
        await ILD.Api.Configuration.TemplateSeeder.SeedAsync(dbContext, mgr);

        // Best-effort recovery: any LoopRun left in Running across restart
        var recovery = scope.ServiceProvider.GetRequiredService<ILD.Core.Services.Interfaces.IRecoveryManager>();
        foreach (var runId in await recovery.GetRecoverableRunIdsAsync())
        {
            try { await recovery.RecoverRunAsync(runId); }
            catch (Exception ex) { Log.Warning(ex, "Recovery failed for run {RunId}", runId); }
        }
    }

    app.UseSerilogRequestLogging();
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
