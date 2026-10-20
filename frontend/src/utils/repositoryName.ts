import type { Repository } from "../types";

/**
 * A repository id in words: its name; "Unknown repository" once the loaded list
 * is known not to hold it, never the bare id; and null until the list has
 * loaded, so nothing is claimed either way.
 */
export function repositoryName(
  id: string,
  repositories: readonly Repository[],
  loaded: boolean,
): string | null {
  const repo = repositories.find((r) => r.id === id);
  if (repo) return repo.name;
  return loaded ? "Unknown repository" : null;
}
