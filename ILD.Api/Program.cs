using ILD.Data;
using ILD.Data.Entities;
using ILD.Api.Authentication;
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

    // The run drain happens inside the host stop, so the host must be willing to
    // wait strictly longer than the drain (its own default is 30s regardless of
    // what the drain was configured to take). Configured off the same
    // ShutdownOptions singleton the drain reads, so the two budgets cannot drift.
    builder.Services.AddOptions<HostOptions>()
        .Configure<ILD.Api.Services.ShutdownOptions>((host, shutdown) =>
            host.ShutdownTimeout = shutdown.HostShutdownTimeout);

    builder.Services.AddSingleton<LoggingLevelSwitch>(loggingLevelSwitch);

    builder.Services.AddControllers()
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
            o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        });

    builder.Services.AddIldAuthentication();

    builder.Services.AddSignalR();

    // Direct forwarder only — a preview's destination is runtime state (a port
    // allocated when someone pressed Start), so there is no route/cluster config
    // for YARP's routing layer to hold.
    builder.Services.AddHttpForwarder();

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
            // Read the retired Users.SessionToken column before the schema
            // migration drops it — the sign-ins it holds are carried into
            // UserSession rows below so this deploy logs nobody out. Empty on
            // any database that never had the column.
            var carriedSessions = await ILD.Data.Migrations.UserSessionCarryOverMigrator.CaptureAsync(dbContext);

            if (connectionString != null && connectionString.Length > 0)
            {
                dbContext.Database.Migrate();
            }
            else
            {
                dbContext.Database.EnsureCreated();
            }
            Log.Information("Database ready");

            // The absolute-expiry setting ships with this change, so on the one
            // boot that carries sessions across it cannot yet have been edited:
            // the default is the only value it can have.
            var sessionsCarried = await ILD.Data.Migrations.UserSessionCarryOverMigrator.ApplyAsync(
                dbContext,
                carriedSessions,
                DateTime.UtcNow.AddDays(ILD.Core.Services.Interfaces.AppSettingKeys.DefaultSessionMaxDays));
            if (sessionsCarried > 0)
                Log.Information("Carried {Count} existing sign-in(s) into the sessions table", sessionsCarried);

            // Retire the obsolete AI rejectPattern config on already-seeded
            // databases (the seeder is insert-only, so existing rows are never
            // rewritten). Idempotent: a no-op once every node is migrated.
            var rejectMigrated = await ILD.Data.Migrations.AiRejectPatternMigrator.MigrateAsync(dbContext);
            if (rejectMigrated > 0)
                Log.Information("Migrated {Count} AI node(s) from rejectPattern to named custom edges", rejectMigrated);

            // Convert legacy true/false Condition nodes to the switch model
            // (cases + default edge). The executor only reads the switch shape,
            // so this rewrites persisted legacy rows to keep old loops working;
            // it is the sole bridge. Idempotent once migrated.
            var conditionsMigrated = await ILD.Data.Migrations.ConditionSwitchMigrator.MigrateAsync(dbContext);
            if (conditionsMigrated > 0)
                Log.Information("Migrated {Count} Condition node(s) from true/false to the switch model", conditionsMigrated);

            // Pull any historically offloaded event payloads back inline into the
            // DB. The payload files lived on the ephemeral /app layer, so this also
            // clears dangling paths whose files a redeploy already wiped.
            var payloadsInlined = await ILD.Data.Migrations.EventLogPayloadInliningMigrator.MigrateAsync(dbContext);
            if (payloadsInlined > 0)
                Log.Information("Inlined {Count} offloaded event-log payload(s) into the database", payloadsInlined);

            var agentUser = ILD.Core.Services.Implementations.AgentIsolation.AgentUser;

            if (ILD.Data.Security.SecretProtector.IsEnabled)
                Log.Information("Secret encryption-at-rest is enabled (ILD_SECRET_KEY set)");
            else if (agentUser is not null)
                Log.Warning(
                    "ILD_SECRET_KEY is not set while agent uid isolation is on ({AgentUser}) — provider API keys and webhook secrets sit in the database in plaintext, and the agent is a lower-trust user reaching the same database. Set it.",
                    agentUser);
            else
                Log.Warning("ILD_SECRET_KEY is not set — provider API keys and webhook secrets are stored in plaintext. Set it to enable encryption-at-rest.");

            if (ILD.Data.Security.SessionTokenHasher.IsPeppered)
                Log.Information("Session tokens are hashed with a keyed pepper (ILD_SESSION_TOKEN_PEPPER set)");
            else
                Log.Warning("ILD_SESSION_TOKEN_PEPPER is not set — session tokens are hashed unkeyed, so anyone who can write the UserSessions table can mint a sign-in. Set it (setting it signs every device out once).");

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

    // Send WebSocket keepalive (Ping) frames well inside the idle timeout of
    // typical reverse proxies, so long-lived interactive terminals don't get
    // torn down as idle and surface to the browser as an abnormal 1006 close.
    // Registered here so the preview proxy below can pass upgrades through.
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

    // Worktree previews are served on wildcard subdomains of
    // ILD_PREVIEW_PROXY_BASE, and this sits ahead of everything that would claim
    // or rewrite their responses: the SPA's static files and fallback (which in a
    // built image would answer a preview request with ILD's own index.html),
    // authentication (a preview is a foreign app and cannot carry an ILD session
    // token), CORS (which terminates preflights and would answer on the preview's
    // behalf), and the security headers, whose deliberately strict
    // `default-src 'self'` CSP is right for the ILD UI and wrong for somebody
    // else's application. It matches only hostnames under the configured base —
    // every other request, the whole UI and API included, passes through
    // untouched. Unset the variable to switch it off.
    app.UseMiddleware<PreviewProxyMiddleware>();

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

    // Authorization is per-endpoint from here on, under a user-only fallback
    // policy (see ILD.Api/Authentication/IldAuthentication.cs): every endpoint
    // below is user-only unless it says otherwise. Static files are served above
    // this point and so stay anonymous, as they must for the SPA shell to load.
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHub<LoopRunHub>("/hubs/loop-run");
    app.MapHub<WorkItemHub>("/hubs/work-item");
    app.MapHub<ChatHub>("/hubs/chat");

    if (Directory.Exists(wwwroot))
    {
        // An unknown path under a non-SPA prefix is a 404 for a client that
        // expects JSON, not ILD's index.html: the SPA fallback below is a
        // catch-all and would otherwise answer them with a 200 and a page. Left
        // under the fallback policy, so an unknown path still tells an anonymous
        // caller nothing.
        foreach (var prefix in new[] { "/api", "/hubs", "/metrics" })
        {
            app.MapFallback($"{prefix}/{{**rest}}", () => Results.NotFound());
        }

        // The SPA shell itself is anonymous — it is what renders the login screen.
        app.MapFallbackToFile("index.html").AllowAnonymous();
    }

    await app.RunAsync();
}
finally
{
    Log.CloseAndFlush();
}

// Required for WebApplicationFactory<Program> in integration tests.
public partial class Program;
