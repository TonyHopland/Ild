import { describe, expect, test } from "vite-plus/test";
import { WorkItem, WorkItemPriority, WorkItemStatus } from "../../types";
import {
  EMPTY_TASKBOARD_FILTER,
  compareTags,
  isFilterActive,
  matchesTaskboardFilter,
  sameTag,
  type TaskboardFilter,
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

/** The ids of the items the filter keeps. */
function kept(items: WorkItem[], filter: TaskboardFilter): string[] {
  return items.filter((item) => matchesTaskboardFilter(item, filter)).map((item) => item.id);
}

describe("matchesTaskboardFilter", () => {
  test("returns every item when the filter is empty", () => {
    const items = [makeItem({ id: "a" }), makeItem({ id: "b" })];
    expect(kept(items, EMPTY_TASKBOARD_FILTER)).toEqual(["a", "b"]);
  });

  test("matches search against title, description and id, case-insensitively", () => {
    const items = [
      makeItem({ id: "a", title: "Add login page" }),
      makeItem({ id: "b", title: "Other", description: "fix the LOGIN bug" }),
      makeItem({ id: "login-123", title: "Unrelated" }),
      makeItem({ id: "d", title: "Nothing here" }),
    ];
    const result = kept(items, { ...EMPTY_TASKBOARD_FILTER, search: "login" });
    expect(result).toEqual(["a", "b", "login-123"]);
  });

  test("matches search within one field, never across the boundary between fields", () => {
    const items = [makeItem({ id: "a", title: "Ends in alpha", description: "beta starts here" })];
    expect(kept(items, { ...EMPTY_TASKBOARD_FILTER, search: "alpha beta" })).toEqual([]);
  });

  test("filters by repository", () => {
    const items = [
      makeItem({ id: "a", repositoryId: "repo-1" }),
      makeItem({ id: "b", repositoryId: "repo-2" }),
    ];
    const result = kept(items, { ...EMPTY_TASKBOARD_FILTER, repositoryId: "repo-2" });
    expect(result).toEqual(["b"]);
  });

  test("filters by tags with AND semantics", () => {
    const items = [
      makeItem({ id: "a", tags: ["frontend", "urgent"] }),
      makeItem({ id: "b", tags: ["frontend"] }),
      makeItem({ id: "c", tags: ["urgent"] }),
    ];
    const result = kept(items, {
      ...EMPTY_TASKBOARD_FILTER,
      tags: ["frontend", "urgent"],
    });
    expect(result).toEqual(["a"]);
  });

  test("matches tags case-insensitively, as the server does", () => {
    const items = [makeItem({ id: "a", tags: ["Frontend", "URGENT"] })];
    const result = kept(items, {
      ...EMPTY_TASKBOARD_FILTER,
      tags: ["frontend", "urgent"],
    });
    expect(result).toEqual(["a"]);
  });

  test("combines dimensions with AND", () => {
    const items = [
      makeItem({ id: "a", title: "Login", repositoryId: "repo-1", tags: ["frontend"] }),
      makeItem({ id: "b", title: "Login", repositoryId: "repo-2", tags: ["frontend"] }),
      makeItem({ id: "c", title: "Logout", repositoryId: "repo-1", tags: ["frontend"] }),
    ];
    const result = kept(items, {
      search: "login",
      repositoryId: "repo-1",
      tags: ["frontend"],
    });
    expect(result).toEqual(["a"]);
  });

  test("ignores leading/trailing whitespace in the search term", () => {
    const items = [makeItem({ id: "a", title: "Login" })];
    expect(kept(items, { ...EMPTY_TASKBOARD_FILTER, search: "  login  " })).toEqual(["a"]);
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

describe("tag names", () => {
  test("are one tag whatever their case", () => {
    expect(sameTag("Frontend", "FRONTEND")).toBe(true);
    expect(sameTag("frontend", "backend")).toBe(false);
  });

  test("sort as the server lists them: by upper case, then by code unit", () => {
    expect(["b", "_x", "a", "B", "A"].sort(compareTags)).toEqual(["A", "a", "B", "b", "_x"]);
  });
});
