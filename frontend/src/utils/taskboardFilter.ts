import type { Repository, WorkItem } from "../types";
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

/** A repository the board can filter by, labelled for display. */
export interface RepositoryOption {
  id: string;
  name: string;
}

/** True when any of the filter dimensions would narrow the board. */
export function isFilterActive(filter: TaskboardFilter): boolean {
  return filter.search.trim() !== "" || filter.repositoryId !== "" || filter.tags.length > 0;
}

/**
 * Apply the filter to a list of work items. Dimensions combine with AND: an
 * item is kept only when it satisfies the search text, the selected repository
 * and all selected tags.
 */
export function filterWorkItems(items: WorkItem[], filter: TaskboardFilter): WorkItem[] {
  const query = filter.search.trim().toLowerCase();
  return items.filter((item) => {
    if (filter.repositoryId && item.repositoryId !== filter.repositoryId) return false;
    if (filter.tags.length > 0) {
      const itemTags = parseTags(item);
      if (!filter.tags.every((tag) => itemTags.includes(tag))) return false;
    }
    if (query) {
      const haystack = [item.title, item.description, item.id]
        .filter((value): value is string => typeof value === "string")
        .join(" ")
        .toLowerCase();
      if (!haystack.includes(query)) return false;
    }
    return true;
  });
}

/** The sorted, de-duplicated set of tags present across the given work items. */
export function collectTags(items: WorkItem[]): string[] {
  const tags = new Set<string>();
  for (const item of items) {
    for (const tag of parseTags(item)) tags.add(tag);
  }
  return [...tags].sort((a, b) => a.localeCompare(b));
}

/**
 * The repositories referenced by the given work items, labelled with their name
 * from {@link repositories} (falling back to the id when the repository is not
 * loaded) and sorted for stable display.
 */
export function collectRepositoryOptions(
  items: WorkItem[],
  repositories: Repository[],
): RepositoryOption[] {
  const names = new Map(repositories.map((repo) => [repo.id, repo.name]));
  const ids = new Set<string>();
  for (const item of items) {
    if (item.repositoryId) ids.add(item.repositoryId);
  }
  return [...ids]
    .map((id) => ({ id, name: names.get(id) ?? id }))
    .sort((a, b) => a.name.localeCompare(b.name));
}
