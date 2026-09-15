using ILD.Core.Services.Implementations.Adapters;
using ILD.Data;
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
/// Claude keeps its session files under <c>$HOME/.claude/projects</c>, in the shared
/// credential store the agent can write. ILD restores and saves them only as the
/// agent: a link planted there is replaced or ignored, never followed, and a
/// planted directory never fails the run. Mutates <c>HOME</c>, so it joins the
/// non-parallel environment collection.
/// </summary>
[Collection("EnvironmentPath")]
public sealed class ClaudeCodeSessionFileTests : IAsyncLifetime
{
    private const string SessionId = "claude-sess";

    private readonly string _home = Directory.CreateTempSubdirectory("ild-claude-session-home-").FullName;
    private readonly string _worktree = Directory.CreateTempSubdirectory("ild-claude-session-wt-").FullName;
    private readonly Guid _runId = Guid.NewGuid();
    private string? _previousHome;
    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;

    private string SessionPath => ClaudeCodeAdapter.GetSessionFilePath(_worktree, SessionId)!;

    public async Task InitializeAsync()
    {
        _previousHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", _home);

        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        services.AddScoped<IAdapterSessionSnapshotStore, AdapterSessionSnapshotStore>();
        _services = services.BuildServiceProvider();

        await using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"template-{_runId:N}", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        db.LoopTemplates.Add(template);
        db.LoopTemplateVersions.Add(version);
        db.LoopRuns.Add(new LoopRun
        {
            Id = _runId,
            WorkItemId = Guid.NewGuid().ToString(),
            LoopTemplateVersionId = version.Id,
            Status = LoopRunStatus.Running,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("HOME", _previousHome);
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
        foreach (var dir in new[] { _home, _worktree })
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var nested in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetUnixFileMode(nested, File.GetUnixFileMode(nested) | UnixFileMode.UserWrite); } catch { }
                }
            }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Restore_replaces_a_planted_link_without_touching_its_target()
    {
        await SeedSnapshotAsync("{\"session_id\":\"claude-sess\",\"type\":\"assistant\",\"text\":\"restored\"}\n");
        var victim = Path.Combine(_home, "orchestrator-owned.txt");
        File.WriteAllText(victim, "victim");
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.CreateSymbolicLink(SessionPath, victim);

        await RunAsync();

        Assert.Equal("victim", File.ReadAllText(victim));
        Assert.Null(new FileInfo(SessionPath).LinkTarget);
        Assert.Contains("restored", File.ReadAllText(SessionPath));
    }

    [Fact]
    public async Task Restore_replaces_a_planted_read_only_directory_and_the_run_goes_ahead()
    {
        await SeedSnapshotAsync("{\"session_id\":\"claude-sess\",\"type\":\"assistant\",\"text\":\"restored\"}\n");
        var locked = Directory.CreateDirectory(Path.Combine(SessionPath, "locked")).FullName;
        File.WriteAllText(Path.Combine(locked, "file"), "x");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        await RunAsync();

        Assert.Contains("restored", File.ReadAllText(SessionPath));
    }

    [Fact]
    public async Task Persist_saves_the_session_file_claude_left()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.WriteAllText(SessionPath, "{\"session_id\":\"claude-sess\",\"type\":\"assistant\",\"text\":\"saved\"}\n");

        await RunAsync();

        Assert.Contains("saved", (await SnapshotAsync())?.SessionJson);
    }

    [Fact]
    public async Task Persist_never_reads_through_a_planted_link()
    {
        var secret = Path.Combine(_home, "orchestrator-secret.txt");
        File.WriteAllText(secret, "{\"secret\":\"do-not-copy\"}\n");
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        File.CreateSymbolicLink(SessionPath, secret);

        await RunAsync();

        Assert.Null(await SnapshotAsync());
    }

    private async Task RunAsync()
    {
        var adapter = new ClaudeCodeAdapter(_services.GetRequiredService<IServiceScopeFactory>());
        var result = await adapter.ExecuteAsync(new AgentExecutionContext(
            Provider: new AiProvider
            {
                Name = "claude-test",
                Type = "claude-code",
                BaseUrl = string.Empty,
                Model = string.Empty,
                Config = "{\"binaryPath\":\"/bin/true\"}",
            },
            Prompt: "test prompt",
            RunContext: new LoopRunContext(_runId, "wi", "t", "d", _worktree, "main", new List<string>(), null),
            ExecutionCount: 1,
            Cancel: CancellationToken.None,
            SessionId: SessionId,
            ManageSession: true));

        Assert.True(result.Success, result.Error);
    }

    private async Task SeedSnapshotAsync(string jsonl)
    {
        await using var db = CreateDbContext();
        db.AdapterSessionSnapshots.Add(new AdapterSessionSnapshot
        {
            LoopRunId = _runId,
            AdapterName = "ClaudeCode",
            SessionId = SessionId,
            SessionJson = ClaudeCodeAdapter.WrapJsonl(SessionId, jsonl),
        });
        await db.SaveChangesAsync();
    }

    private async Task<AdapterSessionSnapshot?> SnapshotAsync()
    {
        await using var db = CreateDbContext();
        return await db.AdapterSessionSnapshots.FirstOrDefaultAsync(
            s => s.LoopRunId == _runId && s.AdapterName == "ClaudeCode" && s.SessionId == SessionId);
    }

    private AppDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
}
