using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests;

/// <summary>
/// What a mocked store does for the PR heartbeat's targeted write: it lands, and the run
/// reads back what was written. The real store's behaviour is tested against a database.
/// </summary>
internal static class PollStoreFake
{
    public static void AcceptPollWrites(this Mock<ILoopRunStore> runs, LoopRun run)
        => runs.Setup(s => s.TryRecordPrPollAsync(run.Id, It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid _, Guid _, string? snapshot, string? states) =>
            {
                run.PrSnapshot = snapshot;
                run.PrPolledEdgeStates = states;
                return true;
            });
}
