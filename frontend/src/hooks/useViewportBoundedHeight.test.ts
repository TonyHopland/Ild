import { afterEach, describe, expect, test } from "vite-plus/test";
import { render, screen, act, cleanup } from "@testing-library/react";
import { createElement } from "react";
import { MIN_BOUNDED_HEIGHT_PX, useViewportBoundedHeight } from "./useViewportBoundedHeight";

// jsdom reports getBoundingClientRect().top as 0, so the bounded height reduces
// to `innerHeight - reserveBelow` (clamped) — enough to exercise the formula,
// the resize listener and the min clamp.
function Probe({ reserveBelow }: { reserveBelow: number }) {
  const { ref, maxHeight } = useViewportBoundedHeight<HTMLDivElement>(reserveBelow);
  return createElement("div", { ref, "data-testid": "probe", style: { maxHeight } });
}

function setInnerHeight(height: number) {
  Object.defineProperty(window, "innerHeight", {
    value: height,
    writable: true,
    configurable: true,
  });
}

function resizeTo(height: number) {
  act(() => {
    setInnerHeight(height);
    window.dispatchEvent(new Event("resize"));
  });
}

afterEach(() => {
  cleanup();
  setInnerHeight(768);
});

describe("useViewportBoundedHeight", () => {
  test("caps the height to the viewport minus the reserved space on mount", () => {
    setInnerHeight(1000);
    render(createElement(Probe, { reserveBelow: 224 }));
    expect(screen.getByTestId("probe").style.maxHeight).toBe("776px");
  });

  test("shrinks the cap when the reserved space grows", () => {
    setInnerHeight(1000);
    render(createElement(Probe, { reserveBelow: 500 }));
    expect(screen.getByTestId("probe").style.maxHeight).toBe("500px");
  });

  test("recomputes when the window is resized", () => {
    setInnerHeight(1000);
    render(createElement(Probe, { reserveBelow: 224 }));
    expect(screen.getByTestId("probe").style.maxHeight).toBe("776px");

    resizeTo(600);
    expect(screen.getByTestId("probe").style.maxHeight).toBe("376px");
  });

  test("clamps to a usable minimum in a very short window", () => {
    setInnerHeight(200);
    render(createElement(Probe, { reserveBelow: 224 }));
    // available height (200 - 224) is negative, so it clamps to the minimum.
    expect(screen.getByTestId("probe").style.maxHeight).toBe(`${MIN_BOUNDED_HEIGHT_PX}px`);
  });
});
