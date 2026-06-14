import { describe, expect, test } from "vite-plus/test";
import { Repository, WorkItem, WorkItemPriority, WorkItemStatus } from "../../types";
import {
  EMPTY_TASKBOARD_FILTER,
  collectRepositoryOptions,
  collectTags,
  filterWorkItems,
  isFilterActive,
} from "../taskboardFilter";

function makeItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Test Item",
    description: "desc",
    status: WorkItemStatus.Ready,
    priority: WorkItemPriority.Medium,
    tags: [],
    repositoryId: "repo-1",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: null,
    humanFeedbackActions: null,
    createdAt: "2025-01-01T00:00:00Z",
    startedAt: null,
    completedAt: null,
    currentLoopRunId: null,
    dependencyIds: [],
    dependentIds: [],
    ...overrides,
  };
}

function makeRepo(overrides: Partial<Repository> = {}): Repository {
  return {
    id: "repo-1",
    name: "Repo One",
    remoteProviderId: "rp-1",
    cloneUrl: "https://example.com/repo.git",
    defaultBranch: "main",
    worktreesPath: null,
    defaultIntakeStatus: WorkItemStatus.Backlog,
    createdAt: "2025-01-01T00:00:00Z",
    ...overrides,
  };
}

describe("filterWorkItems", () => {
  test("returns every item when the filter is empty", () => {
    const items = [makeItem({ id: "a" }), makeItem({ id: "b" })];
    expect(filterWorkItems(items, EMPTY_TASKBOARD_FILTER)).toHaveLength(2);
  });

  test("matches search against title, description and id, case-insensitively", () => {
    const items = [
      makeItem({ id: "a", title: "Add login page" }),
      makeItem({ id: "b", title: "Other", description: "fix the LOGIN bug" }),
      makeItem({ id: "login-123", title: "Unrelated" }),
      makeItem({ id: "d", title: "Nothing here" }),
    ];
    const result = filterWorkItems(items, { ...EMPTY_TASKBOARD_FILTER, search: "login" });
    expect(result.map((i) => i.id)).toEqual(["a", "b", "login-123"]);
  });

  test("filters by repository", () => {
    const items = [
      makeItem({ id: "a", repositoryId: "repo-1" }),
      makeItem({ id: "b", repositoryId: "repo-2" }),
    ];
    const result = filterWorkItems(items, { ...EMPTY_TASKBOARD_FILTER, repositoryId: "repo-2" });
    expect(result.map((i) => i.id)).toEqual(["b"]);
  });

  test("filters by tags with AND semantics", () => {
    const items = [
      makeItem({ id: "a", tags: ["frontend", "urgent"] }),
      makeItem({ id: "b", tags: ["frontend"] }),
      makeItem({ id: "c", tags: ["urgent"] }),
    ];
    const result = filterWorkItems(items, {
      ...EMPTY_TASKBOARD_FILTER,
      tags: ["frontend", "urgent"],
    });
    expect(result.map((i) => i.id)).toEqual(["a"]);
  });

  test("combines dimensions with AND", () => {
    const items = [
      makeItem({ id: "a", title: "Login", repositoryId: "repo-1", tags: ["frontend"] }),
      makeItem({ id: "b", title: "Login", repositoryId: "repo-2", tags: ["frontend"] }),
      makeItem({ id: "c", title: "Logout", repositoryId: "repo-1", tags: ["frontend"] }),
    ];
    const result = filterWorkItems(items, {
      search: "login",
      repositoryId: "repo-1",
      tags: ["frontend"],
    });
    expect(result.map((i) => i.id)).toEqual(["a"]);
  });

  test("ignores leading/trailing whitespace in the search term", () => {
    const items = [makeItem({ id: "a", title: "Login" })];
    expect(filterWorkItems(items, { ...EMPTY_TASKBOARD_FILTER, search: "  login  " })).toHaveLength(
      1,
    );
  });
});

describe("isFilterActive", () => {
  test("is false for the empty filter and a whitespace-only search", () => {
    expect(isFilterActive(EMPTY_TASKBOARD_FILTER)).toBe(false);
    expect(isFilterActive({ ...EMPTY_TASKBOARD_FILTER, search: "   " })).toBe(false);
  });

  test("is true when any dimension is set", () => {
    expect(isFilterActive({ ...EMPTY_TASKBOARD_FILTER, search: "x" })).toBe(true);
    expect(isFilterActive({ ...EMPTY_TASKBOARD_FILTER, repositoryId: "repo-1" })).toBe(true);
    expect(isFilterActive({ ...EMPTY_TASKBOARD_FILTER, tags: ["a"] })).toBe(true);
  });
});

describe("collectTags", () => {
  test("returns sorted, de-duplicated tags across items", () => {
    const items = [makeItem({ tags: ["b", "a"] }), makeItem({ tags: ["a", "c"] })];
    expect(collectTags(items)).toEqual(["a", "b", "c"]);
  });
});

describe("collectRepositoryOptions", () => {
  test("labels referenced repositories by name and sorts them", () => {
    const items = [makeItem({ repositoryId: "repo-2" }), makeItem({ repositoryId: "repo-1" })];
    const repos = [
      makeRepo({ id: "repo-1", name: "Beta" }),
      makeRepo({ id: "repo-2", name: "Alpha" }),
    ];
    expect(collectRepositoryOptions(items, repos)).toEqual([
      { id: "repo-2", name: "Alpha" },
      { id: "repo-1", name: "Beta" },
    ]);
  });

  test("falls back to the id when the repository is not loaded", () => {
    const items = [makeItem({ repositoryId: "repo-x" })];
    expect(collectRepositoryOptions(items, [])).toEqual([{ id: "repo-x", name: "repo-x" }]);
  });
});
