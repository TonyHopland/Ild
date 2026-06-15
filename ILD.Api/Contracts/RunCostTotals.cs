using ILD.Data.Entities;

namespace ILD.Api.Contracts;

/// <summary>
/// Per-run token/cost rollup summed from the run's <see cref="LoopRunNode"/>
/// rows. Surfaced on the run-list and run-detail endpoints so the work-item
/// panel can show "this loop cost $X" for a completed run without a separate
/// query. <see cref="TotalCostUsd"/> is null when no node reported a cost.
/// </summary>
public sealed record RunCostTotals(long TotalInputTokens, long TotalOutputTokens, decimal? TotalCostUsd)
{
    public static RunCostTotals From(IEnumerable<LoopRunNode> nodes)
    {
        long input = 0, output = 0;
        decimal cost = 0m;
        var sawCost = false;
        foreach (var n in nodes)
        {
            input += n.InputTokens ?? 0;
            output += n.OutputTokens ?? 0;
            if (n.CostUsd is decimal c)
            {
                cost += c;
                sawCost = true;
            }
        }
        return new RunCostTotals(input, output, sawCost ? cost : null);
    }
}
