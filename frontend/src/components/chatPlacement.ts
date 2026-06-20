// Persisted placement for the chat bubble: where the user dragged the floating
// icon and how large they resized the panel. Both are kept per-browser in
// localStorage and clamped to the current viewport so a bubble dragged to a
// corner — or a panel sized on a wide monitor — can never end up stranded
// off-screen after the window shrinks.

export interface Point {
  x: number;
  y: number;
}

export interface Size {
  width: number;
  height: number;
}

export const FAB_SIZE = 52; // 3.25rem at the 16px root size
export const VIEWPORT_MARGIN = 20; // 1.25rem — matches the original fixed inset

export const DEFAULT_PANEL_SIZE: Size = { width: 384, height: 512 }; // 24rem × 32rem
export const MIN_PANEL_SIZE: Size = { width: 280, height: 320 };

export const FAB_POSITION_KEY = "ild_chat_fab_pos";
export const PANEL_SIZE_KEY = "ild_chat_panel_size";
export const PANEL_POSITION_KEY = "ild_chat_panel_pos";

function clamp(value: number, min: number, max: number): number {
  // When the available range is empty (tiny viewport) fall back to the lower
  // bound rather than letting Math.min/Math.max invert the result.
  if (max < min) return min;
  return Math.min(Math.max(value, min), max);
}

/** Current viewport size, or zeros when there is no `window` (SSR/tests). */
export function viewportSize(): Size {
  return {
    width: typeof window === "undefined" ? 0 : window.innerWidth,
    height: typeof window === "undefined" ? 0 : window.innerHeight,
  };
}

/** Keep the floating icon fully inside the viewport, honouring the edge margin. */
export function clampFabPosition(pos: Point, vp: Size): Point {
  return {
    x: clamp(pos.x, VIEWPORT_MARGIN, vp.width - FAB_SIZE - VIEWPORT_MARGIN),
    y: clamp(pos.y, VIEWPORT_MARGIN, vp.height - FAB_SIZE - VIEWPORT_MARGIN),
  };
}

/** Constrain a requested panel size to the [min, viewport] range. */
export function clampPanelSize(size: Size, vp: Size): Size {
  const maxWidth = Math.max(MIN_PANEL_SIZE.width, vp.width - VIEWPORT_MARGIN * 2);
  const maxHeight = Math.max(MIN_PANEL_SIZE.height, vp.height - VIEWPORT_MARGIN * 2);
  return {
    width: clamp(size.width, MIN_PANEL_SIZE.width, maxWidth),
    height: clamp(size.height, MIN_PANEL_SIZE.height, maxHeight),
  };
}

/** The original lower-right resting spot, used until the user drags the icon. */
export function defaultFabPosition(vp: Size): Point {
  return {
    x: vp.width - FAB_SIZE - VIEWPORT_MARGIN,
    y: vp.height - FAB_SIZE - VIEWPORT_MARGIN,
  };
}

/**
 * Top-left corner for the open panel, clamped so the whole panel stays visible.
 * The anchor is either where the user dragged the window's header or, until
 * then, the icon's position; as the panel grows the clamp pulls it back into
 * view, which keeps a corner panel on-screen.
 */
export function panelPosition(anchor: Point, size: Size, vp: Size): Point {
  return {
    x: clamp(anchor.x, VIEWPORT_MARGIN, vp.width - size.width - VIEWPORT_MARGIN),
    y: clamp(anchor.y, VIEWPORT_MARGIN, vp.height - size.height - VIEWPORT_MARGIN),
  };
}

function readJson(key: string): unknown {
  try {
    const raw = localStorage.getItem(key);
    return raw ? JSON.parse(raw) : null;
  } catch {
    return null;
  }
}

function readPair(value: unknown, a: string, b: string): [number, number] | null {
  if (typeof value !== "object" || value === null) return null;
  const record = value as Record<string, unknown>;
  const first = record[a];
  const second = record[b];
  if (typeof first !== "number" || !Number.isFinite(first)) return null;
  if (typeof second !== "number" || !Number.isFinite(second)) return null;
  return [first, second];
}

/** Load the stored icon position (clamped), or fall back to the default corner. */
export function loadFabPosition(): Point {
  const vp = viewportSize();
  const pair = readPair(readJson(FAB_POSITION_KEY), "x", "y");
  return pair ? clampFabPosition({ x: pair[0], y: pair[1] }, vp) : defaultFabPosition(vp);
}

export function saveFabPosition(pos: Point): void {
  localStorage.setItem(FAB_POSITION_KEY, JSON.stringify(pos));
}

/** Load the stored panel size (clamped), or fall back to the default size. */
export function loadPanelSize(): Size {
  const vp = viewportSize();
  const pair = readPair(readJson(PANEL_SIZE_KEY), "width", "height");
  return clampPanelSize(pair ? { width: pair[0], height: pair[1] } : DEFAULT_PANEL_SIZE, vp);
}

export function savePanelSize(size: Size): void {
  localStorage.setItem(PANEL_SIZE_KEY, JSON.stringify(size));
}

/**
 * Load the panel's own dragged position, or `null` when the user has never
 * moved the window — in that case the panel falls back to anchoring on the icon.
 */
export function loadPanelPosition(): Point | null {
  const pair = readPair(readJson(PANEL_POSITION_KEY), "x", "y");
  return pair ? { x: pair[0], y: pair[1] } : null;
}

export function savePanelPosition(pos: Point): void {
  localStorage.setItem(PANEL_POSITION_KEY, JSON.stringify(pos));
}
