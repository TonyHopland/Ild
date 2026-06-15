import { useEffect, useMemo, useState } from "react";
import { RunAnalyticsOverview, TemplateAnalytics } from "../../types";
import { analyticsService } from "../../services/auth";
import { formatCost, formatPercent, formatTokens } from "../../utils/cost";
import { formatDurationMs } from "../../utils/duration";

function seconds(value: number | null): string {
  if (value == null) return "—";
  return formatDurationMs(value * 1000) ?? "—";
}

/** A labelled horizontal bar, width proportional to value/max. */
function Bar({
  label,
  value,
  max,
  text,
  tone,
}: {
  label: string;
  value: number;
  max: number;
  text: string;
  tone: string;
}) {
  const pct = max > 0 ? Math.max(2, Math.round((value / max) * 100)) : 0;
  return (
    <div className="analytics-bar-row">
      <span className="analytics-bar-label" title={label}>
        {label}
      </span>
      <span className="analytics-bar-track">
        <span className="analytics-bar-fill" style={{ width: `${pct}%`, backgroundColor: tone }} />
      </span>
      <span className="analytics-bar-value">{text}</span>
    </div>
  );
}

function TemplateCard({ t }: { t: TemplateAnalytics }) {
  const cost = formatCost(t.totalCostUsd);
  return (
    <div className="analytics-card">
      <div className="analytics-card-head">
        <span className="analytics-card-title">{t.templateName}</span>
        <span className="analytics-card-sub">{t.totalRuns} runs</span>
      </div>
      <div className="analytics-success">
        <span className="analytics-bar-track">
          <span
            className="analytics-bar-fill"
            style={{ width: `${Math.round(t.successRate * 100)}%`, backgroundColor: "#22c55e" }}
          />
        </span>
        <span className="analytics-success-pct">{formatPercent(t.successRate)} success</span>
      </div>
      <dl className="analytics-stats">
        <div>
          <dt>Completed</dt>
          <dd>{t.completedRuns}</dd>
        </div>
        <div>
          <dt>Failed</dt>
          <dd>{t.failedRuns}</dd>
        </div>
        <div>
          <dt>Cancelled</dt>
          <dd>{t.cancelledRuns}</dd>
        </div>
        <div>
          <dt>Avg / node</dt>
          <dd>{seconds(t.avgNodeSeconds)}</dd>
        </div>
        <div>
          <dt title="Times a node routed down its on_failure edge">on_failure</dt>
          <dd>{t.onFailureRoutings}</dd>
        </div>
        <div>
          <dt title="Times a node routed down a reject-pattern edge">Rejects</dt>
          <dd>{t.rejectRoutings}</dd>
        </div>
        <div>
          <dt>Human turnaround</dt>
          <dd>{seconds(t.avgHumanFeedbackSeconds)}</dd>
        </div>
        <div>
          <dt>Tokens (in / out)</dt>
          <dd>
            {formatTokens(t.totalInputTokens)} / {formatTokens(t.totalOutputTokens)}
          </dd>
        </div>
        <div>
          <dt>Cost</dt>
          <dd>{cost ?? "—"}</dd>
        </div>
      </dl>
    </div>
  );
}

export default function Analytics() {
  const [overview, setOverview] = useState<RunAnalyticsOverview | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    analyticsService
      .getOverview()
      .then((data) => {
        if (!cancelled) setOverview(data);
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : "Failed to load analytics.");
      })
      .finally(() => {
        if (!cancelled) setIsLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const maxCost = useMemo(
    () => Math.max(0, ...(overview?.templates.map((t) => t.totalCostUsd) ?? [])),
    [overview],
  );

  return (
    <div className="page-container analytics">
      <h1 className="page-title">Run Analytics &amp; Cost</h1>

      {isLoading && <p>Loading analytics…</p>}
      {error && (
        <div className="analytics-error" role="alert">
          {error}
        </div>
      )}

      {overview && !error && (
        <>
          <div className="analytics-summary">
            <div className="analytics-summary-card">
              <span className="analytics-summary-value">{overview.totalRuns}</span>
              <span className="analytics-summary-label">Total runs</span>
            </div>
            <div className="analytics-summary-card">
              <span className="analytics-summary-value">
                {formatCost(overview.totalCostUsd) ?? "$0.00"}
              </span>
              <span className="analytics-summary-label">Total AI cost</span>
            </div>
            <div className="analytics-summary-card">
              <span className="analytics-summary-value">
                {formatTokens(overview.totalInputTokens)}
              </span>
              <span className="analytics-summary-label">Input tokens</span>
            </div>
            <div className="analytics-summary-card">
              <span className="analytics-summary-value">
                {formatTokens(overview.totalOutputTokens)}
              </span>
              <span className="analytics-summary-label">Output tokens</span>
            </div>
          </div>

          {overview.templates.length === 0 ? (
            <p className="analytics-empty">
              No run data yet. Metrics appear once loops have executed.
            </p>
          ) : (
            <>
              {maxCost > 0 && (
                <section className="analytics-chart">
                  <h2 className="analytics-section-title">Cost by template</h2>
                  {overview.templates.map((t) => (
                    <Bar
                      key={t.templateId}
                      label={t.templateName}
                      value={t.totalCostUsd}
                      max={maxCost}
                      text={formatCost(t.totalCostUsd) ?? "—"}
                      tone="#3b82f6"
                    />
                  ))}
                </section>
              )}

              <section className="analytics-templates">
                <h2 className="analytics-section-title">Per template</h2>
                <div className="analytics-grid">
                  {overview.templates.map((t) => (
                    <TemplateCard key={t.templateId} t={t} />
                  ))}
                </div>
              </section>
            </>
          )}
        </>
      )}

      <style>{`
        .analytics-summary {
          display: grid;
          grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
          gap: 1rem;
          margin-bottom: 1.5rem;
        }
        .analytics-summary-card {
          display: flex;
          flex-direction: column;
          gap: 0.25rem;
          padding: 1rem;
          background-color: #242438;
          border: 1px solid #2d2d44;
          border-radius: 0.5rem;
        }
        .analytics-summary-value { font-size: 1.5rem; font-weight: 700; color: #fff; }
        .analytics-summary-label { font-size: 0.8rem; color: #a0a0b0; }
        .analytics-section-title { font-size: 1.05rem; font-weight: 600; margin: 0 0 0.75rem; }
        .analytics-chart {
          padding: 1rem;
          background-color: #242438;
          border: 1px solid #2d2d44;
          border-radius: 0.5rem;
          margin-bottom: 1.5rem;
        }
        .analytics-bar-row {
          display: grid;
          grid-template-columns: 160px 1fr 80px;
          align-items: center;
          gap: 0.75rem;
          margin-bottom: 0.5rem;
        }
        .analytics-bar-label {
          font-size: 0.85rem; color: #d0d0e0;
          overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
        }
        .analytics-bar-track {
          display: block; height: 0.75rem; background-color: #1a1a2e;
          border-radius: 0.375rem; overflow: hidden;
        }
        .analytics-bar-fill { display: block; height: 100%; border-radius: 0.375rem; }
        .analytics-bar-value { font-size: 0.8rem; color: #a0a0b0; text-align: right; }
        .analytics-grid {
          display: grid;
          grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
          gap: 1rem;
        }
        .analytics-card {
          padding: 1rem;
          background-color: #242438;
          border: 1px solid #2d2d44;
          border-radius: 0.5rem;
        }
        .analytics-card-head {
          display: flex; justify-content: space-between; align-items: baseline;
          margin-bottom: 0.75rem;
        }
        .analytics-card-title { font-weight: 600; color: #fff; }
        .analytics-card-sub { font-size: 0.8rem; color: #a0a0b0; }
        .analytics-success {
          display: flex; align-items: center; gap: 0.5rem; margin-bottom: 0.75rem;
        }
        .analytics-success .analytics-bar-track { flex: 1; }
        .analytics-success-pct { font-size: 0.8rem; color: #22c55e; white-space: nowrap; }
        .analytics-stats {
          display: grid; grid-template-columns: 1fr 1fr; gap: 0.5rem 1rem; margin: 0;
        }
        .analytics-stats div { display: flex; flex-direction: column; }
        .analytics-stats dt { font-size: 0.7rem; color: #a0a0b0; text-transform: uppercase; letter-spacing: 0.03em; }
        .analytics-stats dd { margin: 0; font-size: 0.95rem; color: #e0e0e0; }
        .analytics-error {
          padding: 0.75rem 1rem; background-color: #3a1a1a; border: 1px solid #7f1d1d;
          border-radius: 0.5rem; color: #fca5a5;
        }
        .analytics-empty { color: #a0a0b0; }
      `}</style>
    </div>
  );
}
