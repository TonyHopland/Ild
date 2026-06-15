/**
 * Compact token count, e.g. "0", "940", "12.3k", "1.2M". Used on the analytics
 * dashboard and work-item run summary where space is tight.
 */
export function formatTokens(count: number | null | undefined): string {
  if (count == null || !Number.isFinite(count) || count < 0) return "0";
  if (count < 1000) return String(Math.round(count));
  if (count < 1_000_000) return `${(count / 1000).toFixed(1)}k`;
  return `${(count / 1_000_000).toFixed(1)}M`;
}

/**
 * USD cost as a dollar string. Sub-cent amounts keep four decimals so a cheap
 * run doesn't render as "$0.00"; everything else uses two. Returns null when no
 * cost was reported (so callers can omit the figure rather than show "$0").
 */
export function formatCost(usd: number | null | undefined): string | null {
  if (usd == null || !Number.isFinite(usd) || usd < 0) return null;
  if (usd > 0 && usd < 0.01) return `$${usd.toFixed(4)}`;
  return `$${usd.toFixed(2)}`;
}

/** Success rate (0..1) as a whole-percent string, e.g. "67%". */
export function formatPercent(fraction: number | null | undefined): string {
  if (fraction == null || !Number.isFinite(fraction)) return "—";
  return `${Math.round(fraction * 100)}%`;
}
