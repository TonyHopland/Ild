using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores;

namespace ILD.Tests;

/// <summary>
/// Two runs that started at the same moment: the latest is the one with the
/// higher Id, on every read, whichever was written first.
/// </summary>
public class LoopRunStoreLatestRunTieTests
{
    private static readonly DateTime SameStart = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Lower = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Higher = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static LoopRunStore Seed(TestDb db, params Guid[] idsInWriteOrder)
    {
        var versionId = RunTimeline.SeedVersion(db);
        foreach (var id in idsInWriteOrder)
            db.Context.LoopRuns.Add(new LoopRun
            {
                Id = id,
                WorkItemId = "WI-1",
                LoopTemplateVersionId = versionId,
                Status = LoopRunStatus.Failed,
                StartedAt = SameStart,
                CompletedAt = SameStart,
                BranchName = "ild/wi-1",
                WorktreePath = "/work/wi-1",
                RecoveryPolicy = RecoveryPolicy.AutoResume,
            });
        db.Context.SaveChanges();
        return new LoopRunStore(db.Fresh());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_latest_run_breaks_a_start_time_tie_by_id(bool lowerWrittenFirst)
    {
        using var db = new TestDb();
        var store = lowerWrittenFirst ? Seed(db, Lower, Higher) : Seed(db, Higher, Lower);

        Assert.Equal(Higher, (await store.GetLatestByWorkItemAsync("WI-1"))!.Id);
        Assert.Equal(Higher, (await store.GetByBranchNameAsync("ild/wi-1"))!.Id);
        Assert.Equal(Higher, (await store.GetByWorktreePathAsync("/work/wi-1"))!.Id);
        Assert.Equal(new[] { Higher, Lower }, (await store.GetAllByWorkItemAsync("WI-1")).Select(r => r.Id));
        Assert.Equal(new[] { Higher, Lower }, (await store.GetByWorkItemPagedAsync("WI-1", 0, 10)).Select(r => r.Id));
    }
}
