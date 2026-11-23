/**
 * Whether two values read from the server say the same thing. Both come from
 * the same serializer, so their keys are in the same order and comparing their
 * JSON is enough.
 */
export function sameData(a: unknown, b: unknown): boolean {
  return a === b || JSON.stringify(a) === JSON.stringify(b);
}
