using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// The database itself holds at most one pending suggestion per source per
/// variable (ignoring case), so two suggestions racing past the supersede step
/// cannot both stay pending. Decided ones and other sources' ones are not limited.
/// </summary>
public sealed class GlobalVariableProposalPendingIndexTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public enum Source { LoopRun, ChatSession }

    private static GlobalVariableProposal Suggestion(Source source, Guid sourceId, string name, GlobalVariableProposalStatus status)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = GlobalVariable.Normalize(name),
            Value = "Suggested text.",
            Rationale = "It reads better.",
            CreatedByLoopRunId = source == Source.LoopRun ? sourceId : null,
            CreatedByChatSessionId = source == Source.ChatSession ? sourceId : null,
            Status = status,
            CreatedAt = DateTime.UtcNow,
        };

    private async Task AddAsync(GlobalVariableProposal proposal)
    {
        await using var db = _db.Fresh();
        db.GlobalVariableProposals.Add(proposal);
        await db.SaveChangesAsync(Ct);
    }

    [Theory]
    [InlineData(Source.LoopRun)]
    [InlineData(Source.ChatSession)]
    public async Task A_second_pending_suggestion_from_one_source_for_one_variable_is_refused(Source source)
    {
        var sourceId = Guid.NewGuid();
        await AddAsync(Suggestion(source, sourceId, "rules", GlobalVariableProposalStatus.Pending));
        await AddAsync(Suggestion(source, sourceId, "rules", GlobalVariableProposalStatus.Superseded));
        await AddAsync(Suggestion(source, sourceId, "rules", GlobalVariableProposalStatus.Rejected));
        await AddAsync(Suggestion(source, sourceId, "style", GlobalVariableProposalStatus.Pending));
        await AddAsync(Suggestion(source, Guid.NewGuid(), "rules", GlobalVariableProposalStatus.Pending));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => AddAsync(Suggestion(source, sourceId, "RULES", GlobalVariableProposalStatus.Pending)));

        await using var db = _db.Fresh();
        Assert.Equal(3, await db.GlobalVariableProposals.CountAsync(p => p.Status == GlobalVariableProposalStatus.Pending, Ct));
    }
}
