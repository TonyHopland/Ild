import { afterEach, describe, expect, test } from "vite-plus/test";
import {
  clampFabPosition,
  clampPanelSize,
  defaultFabPosition,
  FAB_POSITION_KEY,
  FAB_SIZE,
  loadFabPosition,
  loadPanelPosition,
  loadPanelSize,
  MIN_PANEL_SIZE,
  PANEL_POSITION_KEY,
  PANEL_SIZE_KEY,
  panelPosition,
  VIEWPORT_MARGIN,
} from "./chatPlacement";

const VP = { width: 1000, height: 800 };

afterEach(() => {
  localStorage.clear();
});

describe("clampFabPosition", () => {
  test("leaves an in-bounds position untouched", () => {
    expect(clampFabPosition({ x: 300, y: 200 }, VP)).toEqual({ x: 300, y: 200 });
  });

  test("pulls an off-screen position back inside the margin", () => {
    const clamped = clampFabPosition({ x: 5000, y: 5000 }, VP);
    expect(clamped).toEqual({
      x: VP.width - FAB_SIZE - VIEWPORT_MARGIN,
      y: VP.height - FAB_SIZE - VIEWPORT_MARGIN,
    });
  });

  test("clamps negative coordinates up to the margin", () => {
    expect(clampFabPosition({ x: -100, y: -100 }, VP)).toEqual({
      x: VIEWPORT_MARGIN,
      y: VIEWPORT_MARGIN,
    });
  });

  test("falls back to the margin when the viewport is too small for the icon", () => {
    expect(clampFabPosition({ x: 10, y: 10 }, { width: 40, height: 40 })).toEqual({
      x: VIEWPORT_MARGIN,
      y: VIEWPORT_MARGIN,
    });
  });
});

describe("clampPanelSize", () => {
  test("enforces the minimum size", () => {
    expect(clampPanelSize({ width: 10, height: 10 }, VP)).toEqual(MIN_PANEL_SIZE);
  });

  test("caps the size to the viewport less the margins", () => {
    const sized = clampPanelSize({ width: 99999, height: 99999 }, VP);
    expect(sized).toEqual({
      width: VP.width - VIEWPORT_MARGIN * 2,
      height: VP.height - VIEWPORT_MARGIN * 2,
    });
  });
});

describe("panelPosition", () => {
  test("anchors to the icon when the panel fits", () => {
    expect(panelPosition({ x: 100, y: 120 }, { width: 300, height: 300 }, VP)).toEqual({
      x: 100,
      y: 120,
    });
  });

  test("shifts a corner panel back into view", () => {
    const fab = defaultFabPosition(VP);
    const pos = panelPosition(fab, { width: 400, height: 500 }, VP);
    expect(pos.x).toBe(VP.width - 400 - VIEWPORT_MARGIN);
    expect(pos.y).toBe(VP.height - 500 - VIEWPORT_MARGIN);
  });
});

describe("load helpers", () => {
  test("loadFabPosition returns the default corner when nothing is stored", () => {
    // jsdom defaults to 1024×768.
    expect(loadFabPosition()).toEqual(defaultFabPosition({ width: 1024, height: 768 }));
  });

  test("loadFabPosition clamps a stored but now-off-screen position", () => {
    localStorage.setItem(FAB_POSITION_KEY, JSON.stringify({ x: 99999, y: 99999 }));
    const pos = loadFabPosition();
    expect(pos.x).toBe(1024 - FAB_SIZE - VIEWPORT_MARGIN);
    expect(pos.y).toBe(768 - FAB_SIZE - VIEWPORT_MARGIN);
  });

  test("loadPanelSize ignores corrupt JSON and uses the default", () => {
    localStorage.setItem(PANEL_SIZE_KEY, "not json");
    expect(loadPanelSize()).toEqual({ width: 384, height: 512 });
  });

  test("loadPanelPosition returns null when the window was never moved", () => {
    expect(loadPanelPosition()).toBeNull();
  });

  test("loadPanelPosition returns the stored position", () => {
    localStorage.setItem(PANEL_POSITION_KEY, JSON.stringify({ x: 120, y: 90 }));
    expect(loadPanelPosition()).toEqual({ x: 120, y: 90 });
  });

  test("loadPanelPosition ignores a malformed stored value", () => {
    localStorage.setItem(PANEL_POSITION_KEY, JSON.stringify({ x: "nope" }));
    expect(loadPanelPosition()).toBeNull();
  });
});
