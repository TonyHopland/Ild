export function loadErrorMessage(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message) return error.message;
  if (typeof error === "string") return error;
  // The API client rejects with a plain `{ status, message }` object, not an Error.
  const message = (error as { message?: unknown } | null)?.message;
  if (typeof message === "string" && message) return message;
  return fallback;
}
