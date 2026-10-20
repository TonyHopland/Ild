import { useEffect, useMemo, useState } from "react";
import {
  AnalyticsGranularity,
  AnalyticsSeriesPoint,
  ChatAnalytics,
  ProviderAnalytics,
  RunAnalyticsOverview,
  TemplateAnalytics,
} from "../../types";
import { analyticsService } from "../../services/auth";
import { formatCost, formatPercent, formatTokens } from "../../utils/cost";
import { formatDurationMs } from "../../utils/duration";
import { fillBuckets } from "../../utils/analyticsBuckets";

/** One series colour for every chart on the page. */
const SERIES_COLOR = "var(--accent)";

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
  // Nothing is drawn for zero: a stub would read as a small amount.
  const pct = max > 0 && value > 0 ? Math.max(1, Math.round((value / max) * 100)) : 0;
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

/**
 * Cost (or runs) per period as vertical bars on a real time axis: every period
 * from the first to the last has a slot, so one with no runs is a gap, and a
 * period with runs but no cost draws no bar. Hover or focus a slot for its value.
 */
function TimeSeriesChart({
  series,
  granularity,
}: {
  series: AnalyticsSeriesPoint[];
  granularity: AnalyticsGranularity;
}) {
  const byCost = chartByCost(series, (p) => p.costUsd);
  const buckets = fillBuckets(series, granularity);
  const valueOf = (p: AnalyticsSeriesPoint) => (byCost ? p.costUsd : p.runs);
  const max = Math.max(0, ...series.map(valueOf));
  const unit = granularity === "Day" ? "day" : granularity.toLowerCase();
  const describe = (periodStart: string, p: AnalyticsSeriesPoint | null) => {
    const when = formatPeriod(periodStart, granularity);
    if (!p) return `${when}: no runs`;
    const runs = `${p.runs} ${p.runs === 1 ? "run" : "runs"}`;
    return byCost ? `${when}: ${formatCost(p.costUsd) ?? "$0.00"} · ${runs}` : `${when}: ${runs}`;
  };
  // Axis labels: the first and last period, and where a new month (or year) starts.
  const labelled = buckets.map((b, i) => {
    if (i === 0 || i === buckets.length - 1) return true;
    if (granularity === "Day" || granularity === "Week")
      return b.periodStart.slice(5, 7) !== buckets[i - 1].periodStart.slice(5, 7);
    return (
      granularity === "Month" &&
      b.periodStart.slice(0, 4) !== buckets[i - 1].periodStart.slice(0, 4)
    );
  });
  return (
    <section className="analytics-chart">
      <h2 className="analytics-section-title">
        {byCost ? `Cost per ${unit}` : `Runs per ${unit}`}
      </h2>
      <div
        className="analytics-vbars"
        role="list"
        aria-label={byCost ? "Cost over time" : "Runs over time"}
      >
        {buckets.map((b) => {
          const value = b.point ? valueOf(b.point) : 0;
          const pct = max > 0 && value > 0 ? Math.max(1, (value / max) * 100) : 0;
          const text = describe(b.periodStart, b.point);
          return (
            <div
              key={b.periodStart}
              className="analytics-vbar"
              role="listitem"
              tabIndex={0}
              title={text}
              aria-label={text}
              data-tip={text}
            >
              <span style={{ height: `${pct}%`, backgroundColor: SERIES_COLOR }} />
            </div>
          );
        })}
      </div>
      <div className="analytics-axis" aria-hidden="true">
        {buckets.map((b, i) =>
          labelled[i] ? (
            <span
              key={b.periodStart}
              style={{ left: `${((i + 0.5) / buckets.length) * 100}%` }}
              className={i === 0 ? "is-first" : i === buckets.length - 1 ? "is-last" : undefined}
            >
              {formatPeriod(b.periodStart, granularity)}
            </span>
          ) : null,
        )}
      </div>
      <p className="analytics-chart-note">
        Gaps are {unit}s with no runs.
        {byCost ? " A period with runs but no cost draws no bar." : ""}
      </p>
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
          tone={SERIES_COLOR}
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
        <span className="analytics-card-sub">
          {t.totalRuns} {t.totalRuns === 1 ? "run" : "runs"}
        </span>
      </div>
      <div className="analytics-success">
        <span className="analytics-bar-track">
          <span
            className="analytics-bar-fill"
            style={{ width: `${Math.round(t.successRate * 100)}%`, backgroundColor: "#22c55e" }}
          />
        </span>
        <span className="analytics-success-pct">
          {/* No finished run to compute a rate from reads as "—", not as 0%. */}
          {t.completedRuns + t.failedRuns + t.cancelledRuns === 0
            ? "— success"
            : `${formatPercent(t.successRate)} success`}
        </span>
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
          <dt title="Times a node routed down its on_failure edge">On failure</dt>
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

function chatLabel(c: ChatAnalytics): string {
  return c.scheduleId === null ? "Chat" : `Scheduled: ${c.scheduleName}`;
}

function ChatCard({ c }: { c: ChatAnalytics }) {
  // Providers that report no cost (subscription auth) leave it at 0.
  const cost = c.totalCostUsd > 0 ? formatCost(c.totalCostUsd) : null;
  return (
    <div className="analytics-card">
      <div className="analytics-card-head">
        <span className="analytics-card-title">{chatLabel(c)}</span>
        <span className="analytics-card-sub">{c.totalTurns} turns</span>
      </div>
      <dl className="analytics-stats">
        <div>
          <dt>Tokens (in / out)</dt>
          <dd>
            {formatTokens(c.totalInputTokens)} / {formatTokens(c.totalOutputTokens)}
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
    () =>
      Math.max(
        0,
        ...(overview?.templates.map((t) => t.totalCostUsd) ?? []),
        ...(overview?.chats.map((c) => c.totalCostUsd) ?? []),
      ),
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

          {overview.totalRuns === 0 && overview.chats.length === 0 ? (
            <p className="analytics-empty">
              No run data for this filter. Metrics appear once loops have run or chats have reported
              usage.
            </p>
          ) : (
            <>
              {overview.series.length > 0 && (
                <TimeSeriesChart series={overview.series} granularity={overview.granularity} />
              )}

              {overview.providers.length > 0 && <ProviderChart providers={overview.providers} />}

              {maxCost > 0 && (
                <section className="analytics-chart">
                  <h2 className="analytics-section-title">Cost by template and chat</h2>
                  {overview.templates.map((t) => (
                    <Bar
                      key={t.templateId}
                      label={t.templateName}
                      value={t.totalCostUsd}
                      max={maxCost}
                      text={formatCost(t.totalCostUsd) ?? "—"}
                      tone={SERIES_COLOR}
                    />
                  ))}
                  {overview.chats
                    .filter((c) => c.totalCostUsd > 0)
                    .map((c) => (
                      <Bar
                        key={c.scheduleId ?? "chat"}
                        label={chatLabel(c)}
                        value={c.totalCostUsd}
                        max={maxCost}
                        text={formatCost(c.totalCostUsd) ?? "—"}
                        tone="#14b8a6"
                      />
                    ))}
                </section>
              )}

              <section className="analytics-templates">
                <h2 className="analytics-section-title">Per template and chat</h2>
                <div className="analytics-grid">
                  {overview.templates.map((t) => (
                    <TemplateCard key={t.templateId} t={t} />
                  ))}
                  {overview.chats.map((c) => (
                    <ChatCard key={c.scheduleId ?? "chat"} c={c} />
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
        .analytics-gran-btn.active { background-color: var(--accent); color: #fff; }
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
        /* Vertical bars on a real time axis; each slot is a full-height hover target. */
        .analytics-vbars {
          display: flex;
          align-items: flex-end;
          gap: 2px;
          height: 180px;
          border-bottom: 1px solid var(--border);
          margin-top: 0.75rem;
        }

        .analytics-vbar {
          flex: 1;
          min-width: 2px;
          height: 100%;
          display: flex;
          align-items: flex-end;
          position: relative;
          border-radius: 2px 2px 0 0;
        }

        .analytics-vbar > span {
          display: block;
          width: 100%;
          border-radius: 2px 2px 0 0;
        }

        .analytics-vbar:hover,
        .analytics-vbar:focus-visible {
          background: rgba(255, 255, 255, 0.04);
          outline: none;
        }

        .analytics-vbar:hover::after,
        .analytics-vbar:focus-visible::after {
          content: attr(data-tip);
          position: absolute;
          bottom: calc(100% + 6px);
          left: 50%;
          transform: translateX(-50%);
          background: var(--surface-3);
          border: 1px solid var(--border);
          padding: 4px 8px;
          border-radius: 4px;
          font-size: 11px;
          white-space: nowrap;
          z-index: 2;
          color: var(--text);
        }

        .analytics-axis {
          position: relative;
          height: 16px;
          margin-top: 6px;
          font-size: 11px;
          color: var(--text-3);
        }

        .analytics-axis span {
          position: absolute;
          transform: translateX(-50%);
          white-space: nowrap;
        }

        .analytics-axis span.is-first {
          transform: none;
          left: 0 !important;
        }

        .analytics-axis span.is-last {
          transform: translateX(-100%);
          left: 100% !important;
        }

        .analytics-chart-note {
          margin: 8px 0 0;
          font-size: var(--fs-xs);
          color: var(--text-3);
        }

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
