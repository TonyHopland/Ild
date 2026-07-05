import { useLayoutEffect, useRef, useState } from "react";

// Smallest height the bounded element is ever given, so it stays usable even in
// a very short window; content beyond it scrolls.
export const MIN_BOUNDED_HEIGHT_PX = 96;

/**
 * Caps an element's height to the space between its top and the bottom of the
 * viewport, minus `reserveBelow` pixels kept clear for controls rendered under
 * it. The element grows with its content up to that cap and scrolls past it, so
 * whatever sits below it (e.g. action buttons) never leaves the window.
 *
 * Recomputes on mount, on window resize, and whenever `recomputeKey` changes
 * (e.g. the content that shifts the element's starting position).
 */
export function useViewportBoundedHeight<T extends HTMLElement = HTMLDivElement>(
  reserveBelow: number,
  recomputeKey?: unknown,
) {
  const ref = useRef<T>(null);
  const [maxHeight, setMaxHeight] = useState<number>();

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const update = () => {
      const top = el.getBoundingClientRect().top;
      const available = window.innerHeight - top - reserveBelow;
      setMaxHeight(Math.max(MIN_BOUNDED_HEIGHT_PX, available));
    };
    update();
    window.addEventListener("resize", update);
    return () => window.removeEventListener("resize", update);
  }, [reserveBelow, recomputeKey]);

  return { ref, maxHeight };
}
