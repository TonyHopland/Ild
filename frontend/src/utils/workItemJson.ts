import type { WorkItem } from "../types";

/**
 * Get the tag list from a WorkItem. The API returns tags as a plain
 * string array, so this is a simple safe accessor that tolerates
 * null/missing data.
 */
export function parseTags(workItem: Pick<WorkItem, "tags">): string[] {
  return workItem.tags?.filter((t): t is string => typeof t === "string") ?? [];
}

/**
 * Build a predicate that reports whether a tag is a "loop tag" — i.e. it names
 * a loop template the engine could resolve and run. Matching is
 * case-insensitive, mirroring the backend resolver (DbLoopTemplateResolver),
 * which picks a template when a tag equals its name ignoring case. Tags that
 * match no template are ordinary, free-form labels.
 */
export function makeLoopTagMatcher(loopTemplateNames: readonly string[]): (tag: string) => boolean {
  const names = new Set(loopTemplateNames.map((n) => n.toLowerCase()));
  return (tag: string) => names.has(tag.toLowerCase());
}
