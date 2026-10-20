// Persisted placement for the chat bubble: where the user dragged the floating
// icon and how large they resized the panel. Both are kept among the user's
// preferences exactly as the user chose them, and clamped to the viewport only
// when drawn: a bubble dragged to a corner, or a panel sized on a wide monitor,
// stays on-screen while the window is small and goes back to where it was put
// when the window grows again. Whether the chat list is open is kept here too.

import { MOBILE_QUERY } from "../hooks/useIsMobile";
import { PreferenceKeys, getPreference, setPreference } from "../utils/userPreferences";

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

export const FAB_POSITION_KEY = PreferenceKeys.ChatFabPosition;
export const PANEL_SIZE_KEY = PreferenceKeys.ChatPanelSize;
export const PANEL_POSITION_KEY = PreferenceKeys.ChatPanelPosition;

function clamp(value: number, min: number, max: number): number {
  // When the available range is empty (tiny viewport) fall back to the lower
  // bound rather than letting Math.min/Math.max invert the result.
  if (max < min) return min;
  return Math.min(Math.max(value, min), max);
}

/**
 * The phone tab bar's height, without the safe-area inset: the one source for
 * the bar itself (MobileNav) and for the chat icon, which stays above it.
 */
export const MOBILE_TABBAR_HEIGHT = 60;

/**
 * The area the icon and panel may use, or zeros when there is no `window`
 * (SSR/tests): the viewport, less the tab bar on a phone.
 */
export function viewportSize(): Size {
  if (typeof window === "undefined") return { width: 0, height: 0 };
  const tabBar = window.matchMedia?.(MOBILE_QUERY).matches ? MOBILE_TABBAR_HEIGHT : 0;
  return { width: window.innerWidth, height: window.innerHeight - tabBar };
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
    const raw = getPreference(key);
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

/**
 * The icon position the user dragged it to, unclamped, or `null` when it was
 * never moved and sits in the default corner.
 */
export function loadFabPosition(): Point | null {
  const pair = readPair(readJson(FAB_POSITION_KEY), "x", "y");
  return pair ? { x: pair[0], y: pair[1] } : null;
}

/** Where the icon is drawn: the chosen spot pulled into view, or the default corner. */
export function renderedFabPosition(chosen: Point | null, vp: Size): Point {
  return chosen ? clampFabPosition(chosen, vp) : defaultFabPosition(vp);
}

export function saveFabPosition(pos: Point): void {
  setPreference(FAB_POSITION_KEY, JSON.stringify(pos));
}

/** The panel size the user chose, unclamped, or the default size. Clamp it when drawing. */
export function loadPanelSize(): Size {
  const pair = readPair(readJson(PANEL_SIZE_KEY), "width", "height");
  return pair ? { width: pair[0], height: pair[1] } : DEFAULT_PANEL_SIZE;
}

export function savePanelSize(size: Size): void {
  setPreference(PANEL_SIZE_KEY, JSON.stringify(size));
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
  setPreference(PANEL_POSITION_KEY, JSON.stringify(pos));
}

/**
 * The narrowest panel that holds the chat list beside a chat: about the list's
 * width plus the default panel's. Below it an open list fills the whole panel.
 */
export const SIDEBAR_SIDE_BY_SIDE_MIN_WIDTH = 640;
export const SIDEBAR_OPEN_KEY = PreferenceKeys.ChatSidebarOpen;

/** Whether the chat list was left open; until the user says, open only beside a chat. */
export function loadSidebarOpen(panelWidth: number): boolean {
  const stored = readJson(SIDEBAR_OPEN_KEY);
  return typeof stored === "boolean" ? stored : panelWidth >= SIDEBAR_SIDE_BY_SIDE_MIN_WIDTH;
}

export function saveSidebarOpen(open: boolean): void {
  setPreference(SIDEBAR_OPEN_KEY, JSON.stringify(open));
}
