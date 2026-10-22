import { afterEach, expect, test, vi } from "vite-plus/test";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import TaskboardColumn from "./TaskboardColumn";
import { WorkItem, WorkItemStatus } from "../types";
import * as authServices from "../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function item(id: string, status: WorkItemStatus): WorkItem {
  return {
    id,
    title: `Item ${id}`,
    description: "",
    status,
    priority: 0,
    tags: [],
    repositoryId: "repo-1",
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

test("a dropped card is handed back with the card the server placed it directly after", async () => {
  const placed = { ...item("x", WorkItemStatus.Ready), priority: 9 };
  vi.spyOn(authServices.workItemService, "reorder").mockResolvedValue(placed);
  const onWorkItemUpdate = vi.fn();
  const onWorkItemPlaced = vi.fn();
  render(
    <TaskboardColumn
      status={WorkItemStatus.Ready}
      label="Ready"
      workItems={[item("a", WorkItemStatus.Ready), item("b", WorkItemStatus.Ready)]}
      total={30}
      onWorkItemUpdate={onWorkItemUpdate}
      onWorkItemPlaced={onWorkItemPlaced}
      foldChoice={false}
      onFoldChange={() => {}}
    />,
  );

  fireEvent.drop(screen.getByText("Ready").closest("div")!, {
    dataTransfer: { getData: () => "x" },
  } as unknown as DragEvent);

  await waitFor(() => expect(onWorkItemPlaced).toHaveBeenCalledWith(placed, "b"));
  expect(onWorkItemUpdate).not.toHaveBeenCalled();
});
