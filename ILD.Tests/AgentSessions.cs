using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

internal static class AgentSessions
{
    public static IAgentSessionResolver Resolver(TestDb db, IWorkItemManager workItems)
        => new AgentSessionResolver(db.Context, db.LoopRuns, workItems, TestActors.Instance);
}
