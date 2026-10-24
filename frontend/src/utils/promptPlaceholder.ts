/** Client-side mirror of the backend's `PromptPlaceholderRegistry` grammar, shared by every place that scans for placeholders. */

/** Mirrors `PromptPlaceholderRegistry.Pattern`. */
export const PLACEHOLDER = /\{\{\s*([A-Za-z][A-Za-z0-9_.:/\\-]*)\s*\}\}/g;

/** Mirrors `PromptPlaceholderRegistry.VariableNamePattern`. */
export const VARIABLE_NAME = /^[A-Za-z][A-Za-z0-9_]{0,127}$/;
