import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, waitFor } from "@testing-library/react";
import { useWorkItemDetail } from "./useWorkItemDetail";
import * as authServices from "../../services/auth";
import { LoopTemplate, Repository, WorkItem, WorkItemPriority, WorkItemStatus } from "../../types";

afterEach(() => {
  vi.restoreAllMocks();
});

function workItem(n: number): WorkItem {
  return {
    id: `wi-${n}`,
    title: `Item ${n}`,
    description: "",
    status: WorkItemStatus.Backlog,
    priority: WorkItemPriority.Medium,
    tags: [],
    repositoryId: "repo-0",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: null,
    humanFeedbackActions: null,
    createdAt: "2026-01-01T00:00:00Z",
    startedAt: null,
    completedAt: null,
    currentLoopRunId: null,
    dependencyIds: [],
    dependentIds: [],
  };
}

/** A list endpoint's skip/take window over `rows`, capped the way the API caps it. */
function paged<T>(rows: T[]) {
  return async (opts?: { skip?: number; take?: number }) => {
    const skip = opts?.skip ?? 0;
    return rows.slice(skip, skip + Math.min(opts?.take ?? 100, 500));
  };
}

describe("the work item dialog's pickers", () => {
  test("offer every work item, repository and loop template, past one request's cap", async () => {
    const items = Array.from({ length: 501 }, (_, n) => workItem(n));
    const repositories = Array.from({ length: 501 }, (_, n) => ({ id: `repo-${n}` }) as Repository);
    const templates = Array.from({ length: 501 }, (_, n) => ({ id: `t-${n}` }) as LoopTemplate);
    vi.spyOn(authServices.workItemService, "getAll").mockImplementation(paged(items));
    vi.spyOn(authServices.repositoryService, "getAll").mockImplementation(paged(repositories));
    vi.spyOn(authServices.loopTemplateService, "getAll").mockImplementation(paged(templates));
    vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);
    vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([]);
    vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);

    const { result } = renderHook(() => useWorkItemDetail(items[0], () => {}));

    await waitFor(() => expect(result.current.allWorkItems).toHaveLength(501));
    await waitFor(() => expect(result.current.repositories).toHaveLength(501));
    await waitFor(() => expect(result.current.templates).toHaveLength(501));
  });
});
