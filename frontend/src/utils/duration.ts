/**
 * Human-friendly span for a number of milliseconds, e.g. "45s", "3m 12s",
 * "2h 5m". Returns null when the value is negative or not finite.
 */
export function formatDurationMs(ms: number): string | null {
  if (!Number.isFinite(ms) || ms < 0) return null;
  const sec = Math.round(ms / 1000);
  if (sec < 60) return `${sec}s`;
  const min = Math.floor(sec / 60);
  if (min < 60) return `${min}m ${sec % 60}s`;
  return `${Math.floor(min / 60)}h ${min % 60}m`;
}

/**
 * Human-friendly span between two ISO timestamps. Returns null when either
 * bound is missing or the span is negative.
 */
export function formatDuration(start: string | null, end: string | null): string | null {
  if (!start || !end) return null;
  return formatDurationMs(new Date(end).getTime() - new Date(start).getTime());
}
