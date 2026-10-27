using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// The database itself holds at most one pending human-input suggestion per
/// source per work item, so two suggestions racing past the supersede step
/// cannot both stay pending. Decided ones, other sources' and other items' are
/// not limited.
/// </summary>
public sealed class HumanInputProposalPendingIndexTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public enum Source { LoopRun, ChatSession }

    private static HumanInputProposal Suggestion(Source source, Guid sourceId, string workItemId, HumanInputProposalStatus status)
        => new()
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItemId,
            RunId = Guid.NewGuid(),
            RunNodeId = Guid.NewGuid(),
            Output = "OnSuccess",
            Text = "Ship it.",
            Rationale = "The tests pass.",
            CreatedByLoopRunId = source == Source.LoopRun ? sourceId : null,
            CreatedByChatSessionId = source == Source.ChatSession ? sourceId : null,
            Status = status,
            CreatedAt = DateTime.UtcNow,
        };

    private async Task AddAsync(HumanInputProposal proposal)
    {
        await using var db = _db.Fresh();
        db.Set<HumanInputProposal>().Add(proposal);
        await db.SaveChangesAsync(Ct);
    }

    [Theory]
    [InlineData(Source.LoopRun)]
    [InlineData(Source.ChatSession)]
    public async Task A_second_pending_suggestion_from_one_source_for_one_work_item_is_refused(Source source)
    {
        var sourceId = Guid.NewGuid();
        await AddAsync(Suggestion(source, sourceId, "WI-1", HumanInputProposalStatus.Pending));
        await AddAsync(Suggestion(source, sourceId, "WI-1", HumanInputProposalStatus.Superseded));
        await AddAsync(Suggestion(source, sourceId, "WI-1", HumanInputProposalStatus.Approved));
        await AddAsync(Suggestion(source, sourceId, "WI-2", HumanInputProposalStatus.Pending));
        await AddAsync(Suggestion(source, Guid.NewGuid(), "WI-1", HumanInputProposalStatus.Pending));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => AddAsync(Suggestion(source, sourceId, "WI-1", HumanInputProposalStatus.Pending)));

        await using var db = _db.Fresh();
        Assert.Equal(3, await db.Set<HumanInputProposal>().CountAsync(p => p.Status == HumanInputProposalStatus.Pending, Ct));
    }
}
