using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;

namespace ILD.Tests;

/// <summary>
/// The database itself holds at most one pending proposal per source per
/// template, so two proposals racing past the supersede step cannot both stay
/// pending. Decided ones and other sources' ones are not limited.
/// </summary>
public sealed class LoopTemplateProposalPendingIndexTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public enum Source { LoopRun, ChatSession }

    private static LoopTemplateProposal Proposal(Source source, Guid sourceId, Guid templateId, LoopTemplateProposalStatus status)
        => new()
        {
            Id = Guid.NewGuid(),
            LoopTemplateId = templateId,
            BaseVersion = 1,
            Document = """{"$schema":"ild-loop-template/v2","name":"Release Loop","description":"","recoveryPolicy":"AutoResume","nodes":[],"edges":[]}""",
            Rationale = "Builds keep failing.",
            CreatedByLoopRunId = source == Source.LoopRun ? sourceId : null,
            CreatedByChatSessionId = source == Source.ChatSession ? sourceId : null,
            Status = status,
            CreatedAt = DateTime.UtcNow,
        };

    private async Task AddAsync(LoopTemplateProposal proposal)
    {
        await using var db = _db.Fresh();
        db.LoopTemplateProposals.Add(proposal);
        await db.SaveChangesAsync(Ct);
    }

    [Theory]
    [InlineData(Source.LoopRun)]
    [InlineData(Source.ChatSession)]
    public async Task A_second_pending_proposal_from_one_source_for_one_template_is_refused(Source source)
    {
        var sourceId = Guid.NewGuid();
        var template = Guid.NewGuid();
        await AddAsync(Proposal(source, sourceId, template, LoopTemplateProposalStatus.Pending));
        await AddAsync(Proposal(source, sourceId, template, LoopTemplateProposalStatus.Superseded));
        await AddAsync(Proposal(source, sourceId, template, LoopTemplateProposalStatus.Stale));
        await AddAsync(Proposal(source, sourceId, template, LoopTemplateProposalStatus.Applied));
        await AddAsync(Proposal(source, sourceId, template, LoopTemplateProposalStatus.Rejected));
        await AddAsync(Proposal(source, sourceId, Guid.NewGuid(), LoopTemplateProposalStatus.Pending));
        await AddAsync(Proposal(source, Guid.NewGuid(), template, LoopTemplateProposalStatus.Pending));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => AddAsync(Proposal(source, sourceId, template, LoopTemplateProposalStatus.Pending)));

        await using var db = _db.Fresh();
        Assert.Equal(3, await db.LoopTemplateProposals.CountAsync(p => p.Status == LoopTemplateProposalStatus.Pending, Ct));
    }
}
