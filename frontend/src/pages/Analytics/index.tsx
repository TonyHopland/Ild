import { useEffect, useMemo, useState } from "react";
import {
  AnalyticsGranularity,
  AnalyticsSeriesPoint,
  ProviderAnalytics,
  RunAnalyticsOverview,
  TemplateAnalytics,
} from "../../types";
import { analyticsService } from "../../services/auth";
import { formatCost, formatPercent, formatTokens } from "../../utils/cost";
import { formatDurationMs } from "../../utils/duration";

const GRANULARITIES: AnalyticsGranularity[] = ["Day", "Week", "Month", "Year"];
const ALL_PROVIDERS = "";

function seconds(value: number | null): string {
  if (value == null) return "—";
  return formatDurationMs(value * 1000) ?? "—";
}

/** Label a series bucket's start date according to the active granularity. */
function formatPeriod(periodStart: string, granularity: AnalyticsGranularity): string {
  const d = new Date(`${periodStart}T00:00:00Z`);
  if (Number.isNaN(d.getTime())) return periodStart;
  if (granularity === "Year") return String(d.getUTCFullYear());
  if (granularity === "Month")
    return d.toLocaleDateString(undefined, { month: "short", year: "numeric", timeZone: "UTC" });
  const base = d.toLocaleDateString(undefined, { month: "short", day: "numeric", timeZone: "UTC" });
  return granularity === "Week" ? `wk ${base}` : base;
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

/**
 * Cost is the headline metric, but subscription-auth providers report no cost —
 * fall back to run counts so the chart is never empty.
 */
function chartByCost<T>(items: T[], cost: (t: T) => number): boolean {
  return items.some((t) => cost(t) > 0);
}

function TimeSeriesChart({
  series,
  granularity,
}: {
  series: AnalyticsSeriesPoint[];
  granularity: AnalyticsGranularity;
}) {
  const byCost = chartByCost(series, (p) => p.costUsd);
  const max = Math.max(0, ...series.map((p) => (byCost ? p.costUsd : p.runs)));
  return (
    <section className="analytics-chart">
      <h2 className="analytics-section-title">{byCost ? "Cost over time" : "Runs over time"}</h2>
      {series.map((p) => (
        <Bar
          key={p.periodStart}
          label={formatPeriod(p.periodStart, granularity)}
          value={byCost ? p.costUsd : p.runs}
          max={max}
          text={byCost ? (formatCost(p.costUsd) ?? "—") : `${p.runs} runs`}
          tone="#8b5cf6"
        />
      ))}
    </section>
  );
}

function ProviderChart({ providers }: { providers: ProviderAnalytics[] }) {
  const byCost = chartByCost(providers, (p) => p.totalCostUsd);
  const max = Math.max(0, ...providers.map((p) => (byCost ? p.totalCostUsd : p.totalRuns)));
  return (
    <section className="analytics-chart">
      <h2 className="analytics-section-title">By agent provider</h2>
      {providers.map((p) => (
        <Bar
          key={p.provider}
          label={p.provider}
          value={byCost ? p.totalCostUsd : p.totalRuns}
          max={max}
          text={
            byCost
              ? (formatCost(p.totalCostUsd) ?? "—")
              : `${formatTokens(p.totalInputTokens + p.totalOutputTokens)} tok`
          }
          tone="#f59e0b"
        />
      ))}
    </section>
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

  const [granularity, setGranularity] = useState<AnalyticsGranularity>("Day");
  const [provider, setProvider] = useState<string>(ALL_PROVIDERS);
  const [from, setFrom] = useState<string>("");
  const [to, setTo] = useState<string>("");

  useEffect(() => {
    let cancelled = false;
    setIsLoading(true);
    setError(null);
    analyticsService
      .getOverview({
        granularity,
        provider: provider || null,
        from: from || null,
        to: to || null,
      })
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
  }, [granularity, provider, from, to]);

  const maxCost = useMemo(
    () => Math.max(0, ...(overview?.templates.map((t) => t.totalCostUsd) ?? [])),
    [overview],
  );

  return (
    <div className="page-container analytics">
      <h1 className="page-title">Run Analytics &amp; Cost</h1>

      <div className="analytics-filters">
        <div className="analytics-granularity" role="group" aria-label="Time granularity">
          {GRANULARITIES.map((g) => (
            <button
              key={g}
              type="button"
              className={`analytics-gran-btn ${g === granularity ? "active" : ""}`}
              onClick={() => setGranularity(g)}
            >
              {g}
            </button>
          ))}
        </div>
        <label className="analytics-filter">
          <span>Provider</span>
          <select value={provider} onChange={(e) => setProvider(e.target.value)}>
            <option value={ALL_PROVIDERS}>All providers</option>
            {overview?.availableProviders.map((p) => (
              <option key={p} value={p}>
                {p}
              </option>
            ))}
          </select>
        </label>
        <label className="analytics-filter">
          <span>From</span>
          <input
            type="date"
            value={from}
            onChange={(e) => setFrom(e.target.value)}
            max={to || undefined}
          />
        </label>
        <label className="analytics-filter">
          <span>To</span>
          <input
            type="date"
            value={to}
            onChange={(e) => setTo(e.target.value)}
            min={from || undefined}
          />
        </label>
        {(from || to || provider) && (
          <button
            type="button"
            className="analytics-clear"
            onClick={() => {
              setFrom("");
              setTo("");
              setProvider(ALL_PROVIDERS);
            }}
          >
            Clear filters
          </button>
        )}
      </div>

      {isLoading && !overview && <p>Loading analytics…</p>}
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

          {overview.totalRuns === 0 ? (
            <p className="analytics-empty">
              No run data for this filter. Metrics appear once loops have executed.
            </p>
          ) : (
            <>
              {overview.series.length > 0 && (
                <TimeSeriesChart series={overview.series} granularity={overview.granularity} />
              )}

              {overview.providers.length > 0 && <ProviderChart providers={overview.providers} />}

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
        .analytics-filters {
          display: flex;
          flex-wrap: wrap;
          align-items: flex-end;
          gap: 0.75rem 1rem;
          margin-bottom: 1.5rem;
        }
        .analytics-granularity { display: inline-flex; border: 1px solid #2d2d44; border-radius: 0.375rem; overflow: hidden; }
        .analytics-gran-btn {
          padding: 0.4rem 0.8rem; background-color: #242438; color: #a0a0b0;
          border: none; cursor: pointer; font-size: 0.85rem;
        }
        .analytics-gran-btn.active { background-color: #3b82f6; color: #fff; }
        .analytics-filter { display: flex; flex-direction: column; gap: 0.2rem; font-size: 0.75rem; color: #a0a0b0; }
        .analytics-filter select, .analytics-filter input {
          padding: 0.35rem 0.5rem; background-color: #2a2a3e; color: #e0e0e0;
          border: 1px solid #3a3a5c; border-radius: 0.375rem; font-size: 0.85rem;
        }
        .analytics-clear {
          padding: 0.4rem 0.8rem; background-color: transparent; color: #a0a0b0;
          border: 1px solid #3a3a5c; border-radius: 0.375rem; cursor: pointer; font-size: 0.8rem;
        }
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
