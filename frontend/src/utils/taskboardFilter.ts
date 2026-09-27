import type { WorkItem } from "../types";
import { parseTags } from "./workItemJson";

/**
 * A character as the server's OrdinalIgnoreCase compares it: upper-cased on its
 * own, never into several characters ("ß" stays "ß"), with the exceptions .NET
 * makes: dotless ı and long ſ keep their own case, and a Greek letter with
 * iota subscript folds to its titlecase partner ("ᾀ" → "ᾈ"), not to two letters.
 */
function ordinalUpper(char: string): string {
  if (char === "ı" || char === "ſ") return char;
  const cp = char.codePointAt(0)!;
  if (cp >= 0x1f80 && cp <= 0x1faf && cp % 16 < 8) return String.fromCodePoint(cp + 8);
  if (cp === 0x1fb3 || cp === 0x1fc3 || cp === 0x1ff3) return String.fromCodePoint(cp + 9);
  const upper = char.toUpperCase();
  return Array.from(upper).length === 1 ? upper : char;
}

/** Text folded as OrdinalIgnoreCase compares it, so equal folds mean equal to the server. */
function ordinalIgnoreCaseKey(text: string): string {
  return Array.from(text, ordinalUpper).join("");
}

/**
 * Active filter selection for the Taskboard. `search` is matched against a work
 * item's title, description and id; `repositoryId` narrows to a single
 * repository (empty = all); `tags` narrows to items carrying *every* selected
 * tag (AND semantics, so adding tags progressively narrows the board).
 */
export interface TaskboardFilter {
  search: string;
  repositoryId: string;
  tags: string[];
}

export const EMPTY_TASKBOARD_FILTER: TaskboardFilter = {
  search: "",
  repositoryId: "",
  tags: [],
};

/** True when any of the filter dimensions would narrow the board. */
export function isFilterActive(filter: TaskboardFilter): boolean {
  return filter.search.trim() !== "" || filter.repositoryId !== "" || filter.tags.length > 0;
}

/**
 * Whether an item belongs on the board under the filter, by the same rules the
 * server lists columns with: the trimmed search found, case-insensitively, in
 * the title, the description or the id, each on its own; the selected
 * repository; and every selected tag, case-insensitively.
 */
export function matchesTaskboardFilter(item: WorkItem, filter: TaskboardFilter): boolean {
  if (filter.repositoryId && item.repositoryId !== filter.repositoryId) return false;
  const itemTags = parseTags(item);
  if (!filter.tags.every((tag) => itemTags.some((carried) => sameTag(carried, tag)))) return false;
  const query = ordinalIgnoreCaseKey(filter.search.trim());
  if (query) {
    const fields = [item.title, item.description, item.id];
    if (
      !fields.some(
        (field) => typeof field === "string" && ordinalIgnoreCaseKey(field).includes(query),
      )
    )
      return false;
  }
  return true;
}

/** Whether two tag names are one tag: tags match case-insensitively. */
export function sameTag(a: string, b: string): boolean {
  return ordinalIgnoreCaseKey(a) === ordinalIgnoreCaseKey(b);
}

/** Case-insensitively, then by code unit: the order the server lists the tags in use. */
export function compareTags(a: string, b: string): number {
  const ua = ordinalIgnoreCaseKey(a);
  const ub = ordinalIgnoreCaseKey(b);
  if (ua !== ub) return ua < ub ? -1 : 1;
  if (a === b) return 0;
  return a < b ? -1 : 1;
}
