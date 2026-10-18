import { useLayoutEffect, useRef } from "react";

/**
 * `value` as of the last committed render, for effects and callbacks to read
 * when they run, so a value written inline (new every render) neither re-runs
 * an effect nor goes stale in one. Updated before any effect of the render runs.
 */
export function useLatest<T>(value: T): { readonly current: T } {
  const ref = useRef(value);
  useLayoutEffect(() => {
    ref.current = value;
  });
  return ref;
}
