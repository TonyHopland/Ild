import type { LoopTemplateExport } from "../types";

/**
 * Process-wide relay for the loop currently open in the Loop Editor (loop editor
 * context, ADR-0011). The editor and the globally-mounted chat bubble are
 * separate components, so the bubble can't read the editor's live graph through
 * props or context. Instead the editor registers a getter while it is mounted
 * with a loop open, and the bubble pulls the live `ild-loop-template/v1` document
 * at send time — exactly as-of the moment the user hits Send, no mirror.
 */
let provider: (() => LoopTemplateExport | null) | null = null;

/** Register (or, with null, clear) the live-loop getter. The editor owns this. */
export function setOpenLoopProvider(getter: (() => LoopTemplateExport | null) | null): void {
  provider = getter;
}

/**
 * The live loop document the user has open in the Loop Editor, or null when no
 * editor is mounted or no loop is open.
 */
export function getOpenLoopDocument(): LoopTemplateExport | null {
  return provider?.() ?? null;
}
