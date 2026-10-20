import type { AnalyticsGranularity, AnalyticsSeriesPoint } from "../types";

/** One step on the chart's time axis: the period, and its data if it had any runs. */
export interface Bucket {
  periodStart: string;
  point: AnalyticsSeriesPoint | null;
}

const isoDay = (d: Date) => d.toISOString().slice(0, 10);

function next(d: Date, granularity: AnalyticsGranularity): Date {
  const n = new Date(d);
  if (granularity === "Day") n.setUTCDate(n.getUTCDate() + 1);
  else if (granularity === "Week") n.setUTCDate(n.getUTCDate() + 7);
  else if (granularity === "Month") n.setUTCMonth(n.getUTCMonth() + 1);
  else n.setUTCFullYear(n.getUTCFullYear() + 1);
  return n;
}

/**
 * Every period from the first bucket the server returned to the last, in order,
 * so the chart's axis is real time: a period the server has no bucket for had
 * no runs and shows as a gap, rather than the periods either side of it being
 * drawn next to each other. Buckets that do not line up with the steps are kept
 * where they fall; nothing is invented for them.
 */
export function fillBuckets(
  series: AnalyticsSeriesPoint[],
  granularity: AnalyticsGranularity,
  limit = 400,
): Bucket[] {
  if (series.length === 0) return [];
  const sorted = [...series].sort((a, b) => a.periodStart.localeCompare(b.periodStart));
  const byStart = new Map(sorted.map((p) => [p.periodStart, p]));
  const first = new Date(`${sorted[0].periodStart}T00:00:00Z`);
  const last = sorted[sorted.length - 1].periodStart;
  if (Number.isNaN(first.getTime()))
    return sorted.map((p) => ({ periodStart: p.periodStart, point: p }));

  const steps: Bucket[] = [];
  for (let d = first; isoDay(d) <= last && steps.length < limit; d = next(d, granularity)) {
    const key = isoDay(d);
    steps.push({ periodStart: key, point: byStart.get(key) ?? null });
    byStart.delete(key);
  }
  // A bucket off the step grid (or past the limit) still shows, in date order.
  for (const p of byStart.values()) steps.push({ periodStart: p.periodStart, point: p });
  return steps.sort((a, b) => a.periodStart.localeCompare(b.periodStart));
}
