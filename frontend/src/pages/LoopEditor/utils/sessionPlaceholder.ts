/**
 * Client-side mirror of the backend's session-field grammar
 * (`SessionPlaceholderTemplate` / `PromptPlaceholderRegistry`).
 *
 * A session name is a template field, but on a narrower grammar than a prompt:
 * `{{Var.<name>}}` only. Checking it here is purely so the author is told
 * before the save round-trip — the server rejects the same shapes, and the
 * executor is what enforces the run-time half (unset variable, empty or
 * over-long result). Pure, so it unit-tests directly.
 */

/** Mirrors `PromptPlaceholderRegistry.Pattern`. */
const PLACEHOLDER = /\{\{\s*([A-Za-z][A-Za-z0-9_.:/\\-]*)\s*\}\}/g;

/** Mirrors `PromptPlaceholderRegistry.VariablePrefix` + `VariableNamePattern`. */
const VARIABLE_PREFIX = "Var.";
const VARIABLE_NAME = /^[A-Za-z][A-Za-z0-9_]{0,127}$/;

function placeholderNames(value: string): string[] {
  return Array.from(value.matchAll(PLACEHOLDER), (match) => match[1]);
}

function isLoopVariable(name: string): boolean {
  if (!name.toLowerCase().startsWith(VARIABLE_PREFIX.toLowerCase())) return false;
  return VARIABLE_NAME.test(name.slice(VARIABLE_PREFIX.length));
}

/** True when the field interpolates anything at all, i.e. it names one session per value rather than one session. */
export function isTemplatedSessionName(value: string): boolean {
  return placeholderNames(value).length > 0;
}

/**
 * The message to show for a session field that uses a placeholder it may not,
 * or null when the field is fine. `label` is the field's UI name.
 */
export function sessionPlaceholderError(label: string, value: string): string | null {
  const disallowed = placeholderNames(value).filter((name) => !isLoopVariable(name));
  if (disallowed.length === 0) return null;
  return `${label} may only use {{Var.<name>}} placeholders; {{${disallowed[0]}}} is not one.`;
}
