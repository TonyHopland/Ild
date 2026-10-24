using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Claude Code keeps a transcript folder per working directory under
/// <c>$HOME/.claude/projects</c>, and scratch under <c>/tmp/claude-&lt;uid&gt;</c>. Ild
/// copies the transcripts into its database, so they go from disk when the run is
/// cleaned up or the chat is deleted, and a sweeper removes those of working
/// directories that are gone — never one a run or chat still names, or one still
/// being written.
/// </summary>
public sealed class ClaudeProjectFoldersTests : IAsyncLifetime
{
    private readonly TestDb _db = new();
    private readonly string _root = Directory.CreateTempSubdirectory("ild-claude-projects-").FullName;
    private readonly ClaudeProjectFolders _folders;
    private string _agentUid = null!;
    private Guid _versionId;

    public ClaudeProjectFoldersTests()
    {
        _folders = new ClaudeProjectFolders(
            agentUser: null, agentHome: null, new TestProcessEnvironment { { "HOME", Path.Combine(_root, "home") } },
            tempRoot: Path.Combine(_root, "tmp"));
    }

    public async ValueTask InitializeAsync()
    {
        _agentUid = (await AgentWritableFiles.GetUidAsync(TestContext.Current.CancellationToken))!;
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "t", RecoveryPolicy = RecoveryPolicy.AutoResume };
        var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1 };
        _db.Context.LoopTemplates.Add(template);
        _db.Context.LoopTemplateVersions.Add(version);
        await _db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        _versionId = version.Id;
    }

    public ValueTask DisposeAsync()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch { }
        return ValueTask.CompletedTask;
    }

    private string ProjectFolder(string cwd) => Path.Combine(_folders.ProjectsRoot!, ClaudeCodeAdapter.EncodeWorktreePath(cwd));

    private string TempFolder(string cwd) => Path.Combine(_root, "tmp", $"claude-{_agentUid}", ClaudeCodeAdapter.EncodeWorktreePath(cwd));

    /// <summary>A session started in <paramref name="cwd"/>, whose later lines carry <paramref name="laterCwds"/>.</summary>
    private void WriteTranscript(string cwd, bool old = true, params string[] laterCwds)
    {
        var folder = ProjectFolder(cwd);
        Directory.CreateDirectory(Path.Combine(folder, "memory"));
        var lines = new List<string> { """{"type":"queue-operation","operation":"enqueue","sessionId":"s1"}""" };
        foreach (var line in new[] { cwd }.Concat(laterCwds))
            lines.Add(JsonSerializer.Serialize(new { type = "user", cwd = line, sessionId = "s1", message = new { content = "{\"cwd\":\"/somewhere/else\"}" } }));
        File.WriteAllLines(Path.Combine(folder, "s1.jsonl"), lines);

        var scratch = TempFolder(cwd);
        Directory.CreateDirectory(scratch);
        File.WriteAllText(Path.Combine(scratch, "task.output"), "output");

        if (old)
        {
            var then = DateTime.UtcNow.AddHours(-3);
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetLastWriteTimeUtc(file, then);
            foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Append(folder))
                Directory.SetLastWriteTimeUtc(dir, then);
        }
    }

    private string Gone(string name) => Path.Combine(_root, "gone", name);

    private string Existing(string name) => Directory.CreateDirectory(Path.Combine(_root, "here", name)).FullName;

    private async Task<Guid> SeedRunAsync(LoopRunStatus status, string? worktreePath)
    {
        var run = new LoopRun
        {
            Id = Guid.NewGuid(),
            WorkItemId = "WI-1",
            LoopTemplateVersionId = _versionId,
            Status = status,
            RecoveryPolicy = RecoveryPolicy.AutoResume,
            WorktreePath = worktreePath,
            BranchName = worktreePath is null ? null : "ild/wi-1",
        };
        await using var ctx = _db.Fresh();
        ctx.LoopRuns.Add(run);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        return run.Id;
    }

    private async Task<Guid> SeedChatAsync(string scratchPath, DateTime createdAt)
    {
        var id = Guid.NewGuid();
        await using var ctx = _db.Fresh();
        ctx.ChatSessions.Add(new ChatSession
        {
            Id = id,
            UserId = "alice",
            AiProviderId = Guid.NewGuid(),
            ProviderType = "claude-code",
            ToolAllowlistCsv = "ild",
            ScratchPath = scratchPath,
            CreatedAt = createdAt,
        });
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private ChatService NewChatService(AppDbContext ctx)
        => new(ctx, new ProviderStore(ctx), Mock.Of<IAgentAdapterRegistry>(), Mock.Of<IChatNotifier>(),
            new ChatOptions { ScratchRoot = Path.Combine(_root, "chat-sessions") }, new LoopRunStore(ctx),
            new ChatLoopScratchpad(), new ChatCloneCoordinator(), claudeProjects: _folders);

    private ClaudeProjectFolderSweeper NewSweeper()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.Fresh());
        return new ClaudeProjectFolderSweeper(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _folders, NullLogger<ClaudeProjectFolderSweeper>.Instance);
    }

    [Fact]
    public async Task Cleaning_up_a_finished_run_deletes_its_worktree_folders_even_with_dashes_and_dots_in_the_path()
    {
        var worktree = Existing("wi-427-run-a1.b2");
        WriteTranscript(worktree, old: false);
        var runId = await SeedRunAsync(LoopRunStatus.Completed, worktree);
        var reclaimer = new Mock<IRunReclaimer>();
        reclaimer.Setup(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>())).ReturnsAsync(true);

        var run = (await _db.LoopRuns.GetByIdAsync(runId))!;
        var outcome = await RunCleanup.CleanUpAsync(run, reclaimer.Object, _db.LoopRuns, new AdapterSessionSnapshotStore(_db.Context), _folders);

        Assert.Equal(RunCleanupOutcome.Cleaned, outcome);
        Assert.False(Directory.Exists(ProjectFolder(worktree)));
        Assert.False(Directory.Exists(TempFolder(worktree)));
    }

    [Fact]
    public async Task A_run_whose_worktree_could_not_be_reclaimed_keeps_its_transcripts()
    {
        var worktree = Existing("wi-5-run-kept");
        WriteTranscript(worktree, old: false);
        var runId = await SeedRunAsync(LoopRunStatus.Completed, worktree);
        var reclaimer = new Mock<IRunReclaimer>();
        reclaimer.Setup(r => r.ReclaimLocalStateAsync(It.IsAny<LoopRun>())).ReturnsAsync(false);

        var run = (await _db.LoopRuns.GetByIdAsync(runId))!;
        var outcome = await RunCleanup.CleanUpAsync(run, reclaimer.Object, _db.LoopRuns, new AdapterSessionSnapshotStore(_db.Context), _folders);

        Assert.Equal(RunCleanupOutcome.ReclaimFailed, outcome);
        Assert.True(Directory.Exists(ProjectFolder(worktree)));
    }

    [Fact]
    public async Task Deleting_a_chat_deletes_its_transcript_folders()
    {
        var scratch = Existing(Guid.NewGuid().ToString("N"));
        WriteTranscript(scratch, old: false);
        var chatId = await SeedChatAsync(scratch, DateTime.UtcNow);

        await using var ctx = _db.Fresh();
        Assert.True(await NewChatService(ctx).DeleteAsync("alice", chatId, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(ProjectFolder(scratch)));
        Assert.False(Directory.Exists(TempFolder(scratch)));
    }

    [Fact]
    public async Task Auto_deleting_an_inactive_chat_deletes_its_transcript_folders()
    {
        var scratch = Existing(Guid.NewGuid().ToString("N"));
        WriteTranscript(scratch, old: false);
        var chatId = await SeedChatAsync(scratch, DateTime.UtcNow.AddDays(-60));

        await using var ctx = _db.Fresh();
        Assert.True(await NewChatService(ctx).DeleteIfInactiveAsync(chatId, DateTime.UtcNow.AddDays(-30), TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(ProjectFolder(scratch)));
        Assert.False(Directory.Exists(TempFolder(scratch)));
    }

    [Fact]
    public async Task The_sweeper_deletes_the_folders_of_a_working_directory_that_is_gone_even_with_dashes_in_its_path()
    {
        var cwd = Gone("wi-427-run-0c4d.e6");
        WriteTranscript(cwd);

        Assert.Equal(1, await NewSweeper().SweepOnceAsync(TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(ProjectFolder(cwd)));
        Assert.False(Directory.Exists(TempFolder(cwd)));
    }

    [Fact]
    public async Task The_sweeper_goes_by_the_directory_a_session_started_in_not_where_the_agent_moved_to()
    {
        var goneStart = Gone("chat-sessions-a");
        WriteTranscript(goneStart, old: true, Existing("clone"), Path.Combine(goneStart, "ild", "frontend"));
        var liveStart = Existing("chat-sessions-b");
        WriteTranscript(liveStart, old: true, Gone("elsewhere"), Path.Combine(liveStart, "ild", "frontend"));

        Assert.Equal(1, await NewSweeper().SweepOnceAsync(TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(ProjectFolder(goneStart)));
        Assert.True(Directory.Exists(ProjectFolder(liveStart)));
    }

    [Fact]
    public async Task The_sweeper_keeps_what_is_still_in_use_or_cannot_be_traced()
    {
        var existing = Existing("wi-1-run-live");
        WriteTranscript(existing);
        var activeRun = Gone("wi-2-run-active");
        WriteTranscript(activeRun);
        await SeedRunAsync(LoopRunStatus.Running, activeRun);
        var chat = Gone("chat-live");
        WriteTranscript(chat);
        await SeedChatAsync(chat, DateTime.UtcNow);
        var recent = Gone("wi-3-run-just-now");
        WriteTranscript(recent, old: false);
        var memoryOnly = Path.Combine(_folders.ProjectsRoot!, ClaudeCodeAdapter.EncodeWorktreePath(Gone("repo")), "memory");
        Directory.CreateDirectory(memoryOnly);
        File.WriteAllText(Path.Combine(memoryOnly, "MEMORY.md"), "- a note");
        File.SetLastWriteTimeUtc(Path.Combine(memoryOnly, "MEMORY.md"), DateTime.UtcNow.AddDays(-1));
        Directory.SetLastWriteTimeUtc(memoryOnly, DateTime.UtcNow.AddDays(-1));
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(memoryOnly)!, DateTime.UtcNow.AddDays(-1));

        Assert.Equal(0, await NewSweeper().SweepOnceAsync(TestContext.Current.CancellationToken));

        foreach (var cwd in new[] { existing, activeRun, chat, recent })
            Assert.True(Directory.Exists(ProjectFolder(cwd)), cwd);
        Assert.True(File.Exists(Path.Combine(memoryOnly, "MEMORY.md")));
    }
}
