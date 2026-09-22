using ILD.Data.DTOs;
using ILD.Data.Stores.Interfaces;

namespace ILD.Core.Services.Remote;

/// <summary>
/// The one way <c>LoopRun.PrCommentLedger</c> changes.
///
/// Four things write it — the heartbeat's delivery and seed, a read that
/// consumes what it returned, the PR node recording what it posted, and a
/// dropped or refused answer putting its finding back — and every one of them
/// is a read-modify-write over a column the others are moving. The heartbeat is
/// the worst offender: it decides from a copy loaded before a forge fetch that
/// takes seconds, so a blind write at the end of its tick reverts anything that
/// happened in between. A drop landing in that window was silently undone, and
/// the finding it was meant to put back stayed suppressed.
///
/// So each writer states its change as a FUNCTION of the current ledger, and
/// this re-applies it to whatever the row says now when it loses the race.
/// <see cref="ILoopRunStore.UpdateRunAsync"/> leaves the column out entirely,
/// as it does the queue, so a full-row write cannot revert one either.
/// </summary>
public static class PrCommentLedgerWriter
{
    /// <summary>Attempts before giving up; each one re-reads the row it lost to.</summary>
    private const int Attempts = 5;

    /// <summary>
    /// Apply <paramref name="change"/> to the run's ledger and persist it,
    /// retrying against a fresh read when another writer got there first.
    /// <paramref name="change"/> is given the ledger as it stands — null when
    /// the run has none yet — and returns null to mean "nothing to do".
    /// Reports whether anything was written.
    /// </summary>
    public static async Task<bool> MutateAsync(
        ILoopRunStore runs, Guid runId, Func<PrCommentLedger?, PrCommentLedger?> change)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var current = await runs.GetPrCommentLedgerAsync(runId);
            var changed = change(PrCommentLedgerJson.TryParse(current));
            if (changed is null)
                return false;

            var json = PrCommentLedgerJson.Serialize(changed);
            if (string.Equals(json, current, StringComparison.Ordinal))
                return true;

            if (await runs.TrySetPrCommentLedgerAsync(runId, current, json))
                return true;
        }

        return false;
    }
}
