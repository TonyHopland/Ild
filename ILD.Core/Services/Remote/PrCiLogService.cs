using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Stores.Interfaces;

namespace ILD.Core.Services.Remote;

/// <summary>Reads one failing check's log on behalf of an agent. Scoped (owns a DbContext via the stores).</summary>
public interface IPrCiLogService
{
    Task<RemoteCiLog> ReadAsync(string workItemId, string checkId, int tailLines, int offset);
}

/// <summary>
/// The other half of a CI failure reason: the reason names the failing checks
/// and hands out a <see cref="RemotePrCheck.CheckId"/>, and this fetches the log
/// behind one of them when the summary was not enough. The forge credentials
/// live here, not with the agent.
///
/// A check id is only honoured when it appears in the work item's own last PR
/// snapshot, so the tool reads the run's own CI and cannot be pointed at an
/// arbitrary job elsewhere in the forge. That lookup is also what lets an
/// unavailable log answer with the human-facing URL the snapshot recorded —
/// the provider knows only the id it was handed.
/// </summary>
public sealed class PrCiLogService : IPrCiLogService
{
    /// <summary>Defaults and bounds for one window. The error is at the end of a log, so the tail is the default view.</summary>
    public const int DefaultTailLines = 200;
    public const int MaxTailLines = 2000;

    private readonly ILoopRunStore _runs;
    private readonly IRemoteProvider _remote;

    public PrCiLogService(ILoopRunStore runs, IRemoteProvider remote)
    {
        _runs = runs;
        _remote = remote;
    }

    public async Task<RemoteCiLog> ReadAsync(string workItemId, string checkId, int tailLines, int offset)
    {
        tailLines = Math.Clamp(tailLines <= 0 ? DefaultTailLines : tailLines, 1, MaxTailLines);
        offset = Math.Max(0, offset);

        var run = await _runs.GetCurrentByWorkItemAsync(workItemId);
        if (run?.PrUrl is null)
            return RemoteCiLog.Unavailable("This work item's current run has no pull request, so it has no CI checks.");

        var check = PrSnapshotJson.TryParse(run.PrSnapshot)?.FailedChecks?
            .FirstOrDefault(c => string.Equals(c.CheckId, checkId, StringComparison.Ordinal));
        if (check is null)
            return RemoteCiLog.Unavailable(
                $"No failing check with id '{checkId}' on this work item's pull request. The reason text that named the check lists the ids that can be read.");

        var repoUrl = RemotePrUrl.ExtractRepoUrl(run.PrUrl);
        if (repoUrl is null)
            return RemoteCiLog.Unavailable("This work item's pull request URL is not one a provider can be resolved from.");

        var log = await _remote.GetCheckLogAsync(repoUrl, checkId, tailLines, offset);

        // An unavailable log is still an answer: point at where a human can read
        // it, which the snapshot knows and the provider does not.
        return log.Available || check.Url is null
            ? log
            : log with { Message = $"{log.Message} See {check.Url}" };
    }
}
