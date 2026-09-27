import { describe, expect, test } from "vite-plus/test";
import { WorkItem, WorkItemPriority, WorkItemStatus } from "../../types";
import { EMPTY_TASKBOARD_FILTER, filterWorkItems, isFilterActive } from "../taskboardFilter";

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

  test("matches search within one field, never across the boundary between fields", () => {
    const items = [makeItem({ id: "a", title: "Ends in alpha", description: "beta starts here" })];
    expect(filterWorkItems(items, { ...EMPTY_TASKBOARD_FILTER, search: "alpha beta" })).toEqual([]);
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

  test("matches tags case-insensitively, as the server does", () => {
    const items = [makeItem({ id: "a", tags: ["Frontend", "URGENT"] })];
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
