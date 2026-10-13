using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// A rejection reason is written past SaveChanges, so it must be made fit for
/// PostgreSQL text the way the save-time scrub would: without NUL characters.
/// </summary>
public sealed class GlobalVariableProposalRejectTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> SuggestAsync()
    {
        await using var db = _db.Fresh();
        var proposal = new GlobalVariableProposal
        {
            Id = Guid.NewGuid(),
            Name = "rules",
            NormalizedName = GlobalVariable.Normalize("rules"),
            Value = "Suggested text.",
            Rationale = "It reads better.",
            CreatedByLoopRunId = Guid.NewGuid(),
            Status = GlobalVariableProposalStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        db.GlobalVariableProposals.Add(proposal);
        await db.SaveChangesAsync(Ct);
        return proposal.Id;
    }

    [Theory]
    [InlineData("Too\0 vague.\0", "Too vague.")]
    [InlineData(" \0 ", null)]
    public async Task A_rejection_reason_is_stored_without_NUL_characters(string reason, string? stored)
    {
        var id = await SuggestAsync();

        var result = await new GlobalVariableProposalStore(_db.Context).RejectAsync(id, reason, Ct);

        Assert.Equal(GlobalVariableProposalDecisionOutcome.Rejected, result.Outcome);
        await using var db = _db.Fresh();
        Assert.Equal(stored, await db.GlobalVariableProposals.Where(p => p.Id == id).Select(p => p.RejectionReason).SingleAsync(Ct));
    }
}
