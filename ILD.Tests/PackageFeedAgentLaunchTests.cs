using System.Diagnostics;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests;

/// <summary>
/// The launch environment an AI node hands its adapter reaches every process the
/// adapter starts for that execution: the agent CLI itself for each adapter, and
/// OpenCode's session export/import launches around it. A stand-in CLI records
/// what it was started with.
/// </summary>
public sealed class PackageFeedAgentLaunchTests : IDisposable
{
    private const string UserConfigValue = "/feeds/ild-test-userconfig.npmrc";
    private const string EndpointsValue = "{\"endpointCredentials\":[{\"endpoint\":\"https://pkgs.dev.azure.com/example-org/_packaging/company/nuget/v3/index.json\",\"username\":\"ild\",\"password\":\"p\"}]}";

    private static readonly IReadOnlyDictionary<string, string> FeedEnvironment = new Dictionary<string, string>
    {
        ["NPM_CONFIG_USERCONFIG"] = UserConfigValue,
        ["VSS_NUGET_EXTERNAL_FEED_ENDPOINTS"] = EndpointsValue,
    };

    private readonly string _dir = Directory.CreateTempSubdirectory("ild-feed-agent-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Log => Path.Combine(_dir, "launches.log");

    private const string Record =
        "printf '%s|%s|%s\\n' \"$1\" \"$NPM_CONFIG_USERCONFIG\" \"$VSS_NUGET_EXTERNAL_FEED_ENDPOINTS\"";

    private string WriteCli(string body)
    {
        var path = Path.Combine(_dir, "fake-agent.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        var psi = new ProcessStartInfo("chmod") { UseShellExecute = false };
        psi.ArgumentList.Add("+x");
        psi.ArgumentList.Add(path);
        using var chmod = Process.Start(psi)!;
        chmod.WaitForExit();
        return path;
    }

    private string[] Launches() => File.ReadAllLines(Log);

    private static void AssertCarriesTheFeeds(string launch)
    {
        var fields = launch.Split('|', 3);
        Assert.Equal(UserConfigValue, fields[1]);
        Assert.Equal(EndpointsValue, fields[2]);
    }

    private AgentExecutionContext Context(string providerType, string binaryPath, Guid? runId = null,
        string? sessionId = null, bool manageSession = false)
        => new(
            Provider: new AiProvider
            {
                Id = Guid.NewGuid(),
                Name = $"{providerType}-test",
                Type = providerType,
                BaseUrl = providerType == "opencode" ? "http://localhost:1234/v1" : string.Empty,
                Model = "m",
                Config = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["binaryPath"] = binaryPath }),
            },
            Prompt: "go",
            RunContext: new LoopRunContext(runId ?? Guid.NewGuid(), "WI-1", "T", "D", _dir, "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: TestContext.Current.CancellationToken,
            SessionId: sessionId,
            ManageSession: manageSession,
            Environment: FeedEnvironment);

    public static TheoryData<string> Adapters => new() { "claude-code", "copilot", "pi", "opencode" };

    [Theory]
    [MemberData(nameof(Adapters))]
    public async Task Every_adapter_launches_its_agent_cli_with_the_run_environment(string providerType)
    {
        IAgentAdapter adapter = providerType switch
        {
            "claude-code" => new ClaudeCodeAdapter(),
            "copilot" => new CopilotAdapter(),
            "pi" => new PiAdapter(),
            _ => new OpenCodeAdapter(),
        };
        var turn = providerType switch
        {
            "claude-code" => PromptCapturingCli.ClaudeCodeTurn,
            // pi takes its prompt on stdin, so the stand-in drains it.
            "pi" => "cat > /dev/null\n" + PromptCapturingCli.PiTurn,
            _ => PromptCapturingCli.SilentSuccess,
        };
        var cli = WriteCli($"{Record} >> '{Log}'\n{turn}");

        var result = await adapter.ExecuteAsync(Context(providerType, cli));

        Assert.True(result.Success, result.Error);
        AssertCarriesTheFeeds(Assert.Single(Launches()));
    }

    [Fact]
    public async Task OpenCode_session_export_and_import_launches_get_the_run_environment_too()
    {
        var ready = Path.Combine(_dir, "session-ready");
        var cli = WriteCli(
            $"{Record} >> '{Log}'\n"
            + "cmd=\"$1\"\n"
            + "shift\n"
            + "case \"$cmd\" in\n"
            + "  import) cat \"$1\" > /dev/null ;;\n"
            + $"  run) touch '{ready}'; echo '{{\"text\":\"still here\"}}' ;;\n"
            + $"  export) if [ ! -f '{ready}' ]; then exit 1; fi; printf '%s' '{{\"id\":\"resume-session\",\"messages\":[2]}}' ;;\n"
            + "esac\n");

        await using var harness = await SessionHarness.CreateAsync();
        var runId = Guid.NewGuid();
        await harness.SeedRunWithSnapshotAsync(runId, "OpenCode", "resume-session", "{\"id\":\"resume-session\",\"messages\":[1]}");
        var adapter = new OpenCodeAdapter(harness.Services.GetRequiredService<IServiceScopeFactory>());

        var result = await adapter.ExecuteAsync(Context("opencode", cli, runId, sessionId: "resume-session", manageSession: true));

        Assert.True(result.Success, result.Error);
        var launches = Launches();
        Assert.Contains(launches, l => l.StartsWith("import|"));
        Assert.Contains(launches, l => l.StartsWith("run|"));
        Assert.True(launches.Count(l => l.StartsWith("export|")) >= 2, string.Join('\n', launches));
        Assert.All(launches, AssertCarriesTheFeeds);
    }

    private sealed class SessionHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly SqliteConnection _connection;

        private SessionHarness(ServiceProvider provider, SqliteConnection connection)
        {
            _provider = provider;
            _connection = connection;
        }

        public IServiceProvider Services => _provider;

        public static async Task<SessionHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Filename=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
            services.AddScoped<IAdapterSessionSnapshotStore, AdapterSessionSnapshotStore>();
            var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
            return new SessionHarness(provider, connection);
        }

        public async Task SeedRunWithSnapshotAsync(Guid runId, string adapterName, string sessionId, string sessionJson)
        {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var templateId = Guid.NewGuid();
            var versionId = Guid.NewGuid();
            db.LoopTemplates.Add(new LoopTemplate { Id = templateId, Name = $"template-{runId:N}", RecoveryPolicy = RecoveryPolicy.AutoResume });
            db.LoopTemplateVersions.Add(new LoopTemplateVersion { Id = versionId, LoopTemplateId = templateId, VersionNumber = 1 });
            db.LoopRuns.Add(new LoopRun
            {
                Id = runId,
                WorkItemId = Guid.NewGuid().ToString(),
                LoopTemplateVersionId = versionId,
                Status = LoopRunStatus.Running,
                RecoveryPolicy = RecoveryPolicy.AutoResume,
            });
            db.AdapterSessionSnapshots.Add(new AdapterSessionSnapshot
            {
                LoopRunId = runId,
                AdapterName = adapterName,
                SessionId = sessionId,
                SessionJson = sessionJson,
            });
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
