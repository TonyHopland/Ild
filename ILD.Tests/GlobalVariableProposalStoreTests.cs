using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ILD.Tests;

/// <summary>
/// What the database decides for the proposal store: a rejection reason written
/// past SaveChanges must be fit for PostgreSQL text, and a suggestion that loses
/// the pending-index race to its own source's others is retried, a few times
/// and only for that race.
/// </summary>
public sealed class GlobalVariableProposalStoreTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly List<AppDbContext> _contexts = new();

    public void Dispose()
    {
        foreach (var context in _contexts) context.Dispose();
        _db.Dispose();
    }

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

    /// <summary>Fails the next saves the way PostgreSQL does, with the given SQLSTATE.</summary>
    private sealed class FailingSaves(string sqlState, int failures) : SaveChangesInterceptor
    {
        public int Saves { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (++Saves <= failures)
                throw new DbUpdateException("save failed", new PostgresException("refused", "ERROR", "ERROR", sqlState));
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private (Func<Task<GlobalVariableProposal>> Propose, FailingSaves Saves) StoreFailing(string sqlState, int failures)
    {
        var saves = new FailingSaves(sqlState, failures);
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_db.Context.Database.GetDbConnection())
            .AddInterceptors(saves)
            .Options);
        _contexts.Add(db);
        var store = new GlobalVariableProposalStore(db);
        return (() => store.ProposeAsync(new GlobalVariableProposal
        {
            Name = "rules",
            Value = "Suggested text.",
            Rationale = "It reads better.",
            CreatedByLoopRunId = Guid.NewGuid(),
        }, Ct), saves);
    }

    [Fact]
    public async Task A_suggestion_that_keeps_losing_the_pending_index_race_is_retried_until_it_lands()
    {
        var (propose, saves) = StoreFailing(PostgresErrorCodes.UniqueViolation, failures: 2);

        var proposal = await propose();

        Assert.Equal(3, saves.Saves);
        await using var db = _db.Fresh();
        Assert.Equal(GlobalVariableProposalStatus.Pending,
            await db.GlobalVariableProposals.Where(p => p.Id == proposal.Id).Select(p => p.Status).SingleAsync(Ct));
    }

    [Fact]
    public async Task The_retries_are_bounded()
    {
        var (propose, saves) = StoreFailing(PostgresErrorCodes.UniqueViolation, failures: 10);

        await Assert.ThrowsAsync<DbUpdateException>(propose);

        Assert.Equal(3, saves.Saves);
    }

    [Fact]
    public async Task Any_other_database_failure_is_not_retried()
    {
        var (propose, saves) = StoreFailing(PostgresErrorCodes.ForeignKeyViolation, failures: 1);

        await Assert.ThrowsAsync<DbUpdateException>(propose);

        Assert.Equal(1, saves.Saves);
    }
}
