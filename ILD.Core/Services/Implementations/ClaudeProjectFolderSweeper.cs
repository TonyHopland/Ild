using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Deletes the Claude Code folders (<see cref="ClaudeProjectFolders"/>) of working
/// directories that are gone: worktrees of runs deleted without a cleanup, chat
/// folders, preview scratch, probes in <c>/tmp</c>. Run cleanup and chat deletion
/// remove their own; this catches what they never saw.
///
/// A folder is kept while any directory its sessions started in exists, while a
/// run row still names one as its worktree or a chat row as its folder, and while
/// anything in it was written in the last <see cref="Quiet"/> — a turn still
/// running, or one whose transcript is not yet in the database.
/// </summary>
public sealed class ClaudeProjectFolderSweeper : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan Quiet = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ClaudeProjectFolders _folders;
    private readonly ILogger<ClaudeProjectFolderSweeper> _log;

    public ClaudeProjectFolderSweeper(IServiceScopeFactory scopes, ClaudeProjectFolders folders, ILogger<ClaudeProjectFolderSweeper> log)
    {
        _scopes = scopes;
        _folders = folders;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = InitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return; }
            delay = SweepInterval;

            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Claude Code folder sweep failed");
            }
        }
    }

    /// <summary>One pass; returns the number of folders deleted.</summary>
    internal async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        var candidates = await _folders.ListQuietAsync(Quiet, ct);
        if (candidates.Count == 0) return 0;

        HashSet<string> tracked;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var worktrees = await db.LoopRuns.Where(r => r.WorktreePath != null).Select(r => r.WorktreePath!).ToListAsync(ct);
            var chats = await db.ChatSessions.Where(c => c.ScratchPath != null).Select(c => c.ScratchPath!).ToListAsync(ct);
            tracked = worktrees.Concat(chats).Select(Normalize).ToHashSet(StringComparer.Ordinal);
        }

        var deleted = 0;
        long freed = 0;
        foreach (var (_, startingCwds) in candidates)
        {
            if (ct.IsCancellationRequested) break;
            if (startingCwds.Any(cwd => tracked.Contains(Normalize(cwd)))) continue;
            if (await AnyExistsAsync(startingCwds, ct)) continue;

            freed += await _folders.DeleteForAsync(startingCwds[0], ct);
            deleted++;
        }

        if (deleted > 0)
            _log.LogInformation("Claude Code folder sweep deleted {Count} folders of working directories that are gone, freeing {Bytes} bytes", deleted, freed);
        return deleted;
    }

    // Either uid seeing it is enough to keep it: the agent ran there, and Ild made most of these.
    private static async Task<bool> AnyExistsAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        foreach (var path in paths)
        {
            if (AgentWritableFiles.EntryExists(path) || await AgentWritableFiles.EntryExistsAsAgentAsync(path, ct))
                return true;
        }
        return false;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(path);
}
