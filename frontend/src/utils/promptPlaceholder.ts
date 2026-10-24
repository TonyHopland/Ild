/** Client-side mirror of the backend's `PromptPlaceholderRegistry` grammar, shared by every place that scans for placeholders. */

/** Mirrors `PromptPlaceholderRegistry.Pattern`. */
const PLACEHOLDER = /\{\{\s*([A-Za-z][A-Za-z0-9_.:/\\-]*)\s*\}\}/g;

/** The key of every placeholder in `text`, in order of appearance. */
export function placeholderKeys(text: string): string[] {
  return Array.from(text.matchAll(PLACEHOLDER), (match) => match[1]);
}

/** Mirrors `PromptPlaceholderRegistry.VariableNamePattern`. */
export const VARIABLE_NAME = /^[A-Za-z][A-Za-z0-9_]{0,127}$/;
