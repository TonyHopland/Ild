const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;
const WEEK = 7 * DAY;

/**
 * How long ago an ISO timestamp was, as of `now` (ms since the epoch): "just now",
 * "5 min ago", "3 h ago", "2 d ago", and the locale date from a week back.
 */
export function formatRelativeTime(iso: string, now: number): string {
  const then = new Date(iso);
  const elapsed = now - then.getTime();
  if (elapsed < MINUTE) return "just now";
  if (elapsed < HOUR) return `${Math.floor(elapsed / MINUTE)} min ago`;
  if (elapsed < DAY) return `${Math.floor(elapsed / HOUR)} h ago`;
  if (elapsed < WEEK) return `${Math.floor(elapsed / DAY)} d ago`;
  return then.toLocaleDateString();
}
