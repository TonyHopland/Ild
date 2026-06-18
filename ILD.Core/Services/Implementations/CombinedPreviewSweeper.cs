using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Reaps abandoned "preview together" composites. A combined preview is built
/// into a throwaway integration worktree and branch (<c>ild/combined-&lt;ids&gt;</c>)
/// with no backing <see cref="ILD.Data.Entities.LoopRun"/>, so the run-based
/// <see cref="WorktreeRetentionSweeper"/> cannot see it. Without this the only
/// teardown path is the manual stop endpoint, so closing the drawer would leak
/// the worktree and branch forever.
///
/// Each pass tears down, the same way the stop endpoint does (member branches
/// and PRs untouched):
/// <list type="bullet">
/// <item>tracked registry entries idle past the retention window, and</item>
/// <item>integration worktrees left on disk with no live entry (e.g. orphaned by
/// a restart, which empties the in-memory registry) that have sat untouched past
/// the window.</item>
/// </list>
/// The window comes from <c>App:CombinedPreviewRetentionHours</c> (default 24);
/// a value of <c>0</c> disables reclamation.
/// </summary>
public sealed class CombinedPreviewSweeper : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private const double DefaultRetentionHours = 24;

    private readonly CombinedPreviewRegistry _registry;
    private readonly IRepositoryManager _repoManager;
    private readonly IWorktreePreviewService _previewService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CombinedPreviewSweeper>? _log;

    public CombinedPreviewSweeper(
        CombinedPreviewRegistry registry,
        IRepositoryManager repoManager,
        IWorktreePreviewService previewService,
        IConfiguration configuration,
        ILogger<CombinedPreviewSweeper>? log = null)
    {
        _registry = registry;
        _repoManager = repoManager;
        _previewService = previewService;
        _configuration = configuration;
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
                await SweepOnceAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "Combined preview sweep failed");
            }
        }
    }

    internal async Task<int> SweepOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        var retentionHours = ResolveRetentionHours();
        if (retentionHours <= 0) return 0; // reclamation disabled
        var cutoff = nowUtc - TimeSpan.FromHours(retentionHours);
        var reaped = 0;

        // 1) Tracked entries idle past the cutoff.
        foreach (var key in _registry.All().Select(e => e.Key).ToList())
        {
            if (ct.IsCancellationRequested) return reaped;
            if (await CombinedPreviewService.ReapEntryAsync(_registry, _previewService, _repoManager, key, cutoff, ct))
                reaped++;
        }

        // 2) Integration worktrees left on disk with no live entry.
        var combinedRoot = Path.Combine(_repoManager.WorktreesRoot, "ild");
        if (Directory.Exists(combinedRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(combinedRoot, "combined-*").ToList())
            {
                if (ct.IsCancellationRequested) return reaped;
                if (await CombinedPreviewService.ReapOrphanWorktreeAsync(_registry, _previewService, _repoManager, dir, cutoff, ct))
                    reaped++;
            }
        }

        if (reaped > 0)
            _log?.LogInformation("Combined preview sweep reaped {Count} integration worktrees idle before {Cutoff:o}", reaped, cutoff);
        return reaped;
    }

    private double ResolveRetentionHours()
        => double.TryParse(_configuration["App:CombinedPreviewRetentionHours"], out var hours)
            ? hours
            : DefaultRetentionHours;
}
