import type { WorkItem } from "../types";
import { parseTags } from "./workItemJson";

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
  if (filter.tags.length > 0) {
    const itemTags = parseTags(item).map((tag) => tag.toLowerCase());
    if (!filter.tags.every((tag) => itemTags.includes(tag.toLowerCase()))) return false;
  }
  const query = filter.search.trim().toLowerCase();
  if (query) {
    const fields = [item.title, item.description, item.id];
    if (!fields.some((field) => typeof field === "string" && field.toLowerCase().includes(query)))
      return false;
  }
  return true;
}

/** The items that match the filter, in their original order. */
export function filterWorkItems(items: WorkItem[], filter: TaskboardFilter): WorkItem[] {
  return items.filter((item) => matchesTaskboardFilter(item, filter));
}
