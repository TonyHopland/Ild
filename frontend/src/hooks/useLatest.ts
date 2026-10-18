import { useLayoutEffect, useRef } from "react";

/**
 * `value` as of the last committed render, for effects and callbacks to read
 * when they run, so a value written inline (new every render) neither re-runs
 * an effect nor goes stale in one. Updated in a layout effect: passive effects,
 * event handlers and async continuations see the render's value; a layout
 * effect that runs earlier in the same commit would still see the last one.
 */
export function useLatest<T>(value: T): { readonly current: T } {
  const ref = useRef(value);
  useLayoutEffect(() => {
    ref.current = value;
  });
  return ref;
}
