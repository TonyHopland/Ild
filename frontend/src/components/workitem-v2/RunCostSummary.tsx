import { LoopRun } from "../../types";
import { formatCost, formatTokens } from "../../utils/cost";

/**
 * Compact "this loop cost $X" summary for a run, shown on the work-item detail
 * panel. Renders nothing when the run has no token/cost data (e.g. a run with
 * no AI nodes, or before any AI node has reported usage).
 */
export default function RunCostSummary({ run }: { run: LoopRun }) {
  const input = run.totalInputTokens ?? 0;
  const output = run.totalOutputTokens ?? 0;
  const cost = formatCost(run.totalCostUsd);

  if (input === 0 && output === 0 && cost === null) return null;

  return (
    <div className="wiv2-run-cost" aria-label="AI cost and token usage for this run">
      {cost !== null && (
        <span className="wiv2-run-cost-item">
          <span className="wiv2-run-cost-value">{cost}</span>
          <span className="wiv2-run-cost-label">cost</span>
        </span>
      )}
      <span className="wiv2-run-cost-item">
        <span className="wiv2-run-cost-value">{formatTokens(input)}</span>
        <span className="wiv2-run-cost-label">in</span>
      </span>
      <span className="wiv2-run-cost-item">
        <span className="wiv2-run-cost-value">{formatTokens(output)}</span>
        <span className="wiv2-run-cost-label">out</span>
      </span>
      <style>{`
        .wiv2-run-cost {
          display: flex;
          gap: 1.25rem;
          padding: 0.5rem 0.75rem;
          margin-bottom: 0.5rem;
          background-color: #1f1f33;
          border: 1px solid #2d2d44;
          border-radius: 0.375rem;
        }
        .wiv2-run-cost-item { display: flex; align-items: baseline; gap: 0.3rem; }
        .wiv2-run-cost-value { font-size: 0.95rem; font-weight: 600; color: #e0e0e0; }
        .wiv2-run-cost-label { font-size: 0.7rem; color: #a0a0b0; text-transform: uppercase; letter-spacing: 0.03em; }
      `}</style>
    </div>
  );
}
