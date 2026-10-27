using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Core.Services.Implementations;

/// <summary>What a run parked on a person is waiting at, and what a person can answer there.</summary>
public static class ParkedNode
{
    /// <summary>
    /// The execution waiting at the node the run is parked at. Only that one: an
    /// execution left waiting at another node (a retry moved the run on) asks a
    /// question the run no longer is at.
    /// </summary>
    public static LoopRunNode? WaitingVisit(IReadOnlyList<LoopRunNode> runNodes, Guid? currentNodeId)
        => runNodes
            .Where(n => n.Status == LoopRunNodeStatus.WaitingHuman && n.LoopNodeId == currentNodeId)
            .OrderByDescending(n => n.StartedAt ?? DateTime.MinValue)
            .FirstOrDefault();

    /// <summary>The execution <paramref name="run"/> waits on a person at, or null when it is not waiting for one (running, ended or halted).</summary>
    public static LoopRunNode? WaitingVisitOf(LoopRun run, IReadOnlyList<LoopRunNode> runNodes)
        => run.Status == LoopRunStatus.WaitingHuman && !run.IsHalted ? WaitingVisit(runNodes, run.CurrentNodeId) : null;

    /// <summary>
    /// The names of the node's outgoing edges, the outputs an answer can take: a
    /// custom edge by its name, otherwise by its role (OnSuccess, OnFailure).
    /// </summary>
    public static IReadOnlyList<string> Outputs(Guid nodeId, IEnumerable<LoopNodeEdge> edges)
        => edges
            .Where(e => e.SourceNodeId == nodeId)
            .Select(e => e.EdgeType == EdgeType.Custom ? e.Name : e.EdgeType.ToString())
            .OfType<string>()
            .Where(name => name.Length > 0)
            .Distinct()
            .ToList();
}
