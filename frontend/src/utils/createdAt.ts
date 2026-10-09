const SECONDS_AND_FRACTION = /^(.*T\d\d:\d\d:\d\d)(?:\.(\d+))?(.*)$/;

/**
 * A server timestamp as the server orders it, to the 100 ns tick: the whole
 * seconds, which Date.parse reads with their zone, and the fraction as seven
 * digits, which it would round to the millisecond.
 */
function createdAtKey(createdAt: string): [seconds: number, ticks: string] {
  const match = SECONDS_AND_FRACTION.exec(createdAt);
  if (!match) return [Date.parse(createdAt), "0000000"];
  return [Date.parse(match[1] + match[3]), (match[2] ?? "").padEnd(7, "0").slice(0, 7)];
}

/** Oldest first, to the tick, as the server compares the same two times. */
export function compareCreatedAt(a: string, b: string): number {
  const [aSeconds, aTicks] = createdAtKey(a);
  const [bSeconds, bTicks] = createdAtKey(b);
  if (aSeconds !== bSeconds) return aSeconds - bSeconds;
  if (aTicks === bTicks) return 0;
  return aTicks < bTicks ? -1 : 1;
}
