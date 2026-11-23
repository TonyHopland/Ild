/**
 * Whether two values read from the server hold the same data, compared
 * structurally, key order aside. It stops at the first difference. A wrong
 * "different" costs only a re-render, so it is used to skip state updates, not
 * to decide anything that matters.
 */
export function sameData(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (typeof a !== "object" || typeof b !== "object" || a === null || b === null) return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a) && Array.isArray(b))
    return a.length === b.length && a.every((item, i) => sameData(item, b[i]));
  const left = a as Record<string, unknown>;
  const right = b as Record<string, unknown>;
  const keys = Object.keys(left);
  return (
    keys.length === Object.keys(right).length &&
    keys.every((key) => key in right && sameData(left[key], right[key]))
  );
}
