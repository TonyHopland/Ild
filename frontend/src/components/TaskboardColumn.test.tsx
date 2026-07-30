import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import TaskboardColumn from "./TaskboardColumn";
import WorkItemCard from "./WorkItemCard";
import { WorkItem, WorkItemPriority, WorkItemStatus } from "../types";
import * as authServices from "../services/auth";

afterEach(() => {
  cleanup();
  // Service spies are per-test; without this a spy's calls leak into the next
  // test that spies on the same method.
  vi.restoreAllMocks();
});

function makeItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "1",
    title: "Item A",
    description: "desc",
    status: WorkItemStatus.Backlog,
    priority: WorkItemPriority.Medium,
    tags: [],
    loopTemplateId: "",
    loopTemplateVersion: "",
    repositoryId: "",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: null,
    humanFeedbackActions: null,
    createdAt: new Date().toISOString(),
    startedAt: null,
    completedAt: null,
    currentLoopRunId: null,
    dependencyIds: [],
    dependentIds: [],
    ...overrides,
  };
}

describe("TaskboardColumn", () => {
  test("renders the label and item count", () => {
    const items = [makeItem({ id: "1" }), makeItem({ id: "2", title: "Item B" })];
    render(
      <TaskboardColumn
        status={WorkItemStatus.Backlog}
        label="Backlog"
        workItems={items}
        onWorkItemUpdate={() => {}}
      />,
    );

    expect(screen.getByText("Backlog")).toBeTruthy();
    expect(screen.getByText("2")).toBeTruthy();
    expect(screen.getByText("Item A")).toBeTruthy();
    expect(screen.getByText("Item B")).toBeTruthy();
  });

  test("renders zero count when there are no items", () => {
    render(
      <TaskboardColumn
        status={WorkItemStatus.Done}
        label="Done"
        workItems={[]}
        onWorkItemUpdate={() => {}}
      />,
    );

    expect(screen.getByText("Done")).toBeTruthy();
    expect(screen.getByText("0")).toBeTruthy();
  });

  // WI-203 "PR disappears" is observed on the Done column: an item that had a
  // PR badge in Human Feedback loses it the moment it is dragged across. The
  // column has to keep surfacing the PR history its cards carry.
  test("Done column cards keep showing their PR history", () => {
    const { container } = render(
      <TaskboardColumn
        status={WorkItemStatus.Done}
        label="Done"
        workItems={[
          makeItem({
            id: "1",
            status: WorkItemStatus.Done,
            prUrl: null,
            pullRequests: [
              { url: "https://forgejo.example.com/repo/pulls/11", runId: "run-2", merged: true },
              { url: "https://forgejo.example.com/repo/pulls/10", runId: "run-1", merged: false },
            ],
          }),
        ]}
        onWorkItemUpdate={() => {}}
      />,
    );

    expect(container.querySelector(".work-item-pr-history")?.textContent).toContain("2");
  });

  test("renders every item when pageSize is not set", () => {
    const items = Array.from({ length: 8 }, (_, i) =>
      makeItem({ id: String(i), title: `Item ${i}` }),
    );
    render(
      <TaskboardColumn
        status={WorkItemStatus.Done}
        label="Done"
        workItems={items}
        onWorkItemUpdate={() => {}}
      />,
    );

    for (let i = 0; i < 8; i++) {
      expect(screen.getByText(`Item ${i}`)).toBeTruthy();
    }
    expect(screen.queryByRole("button", { name: "Load more" })).toBeFalsy();
  });

  test("paginates to pageSize and reveals more on each Load more click", () => {
    const items = Array.from({ length: 12 }, (_, i) =>
      makeItem({ id: String(i), title: `Item ${i}` }),
    );
    render(
      <TaskboardColumn
        status={WorkItemStatus.Backlog}
        label="Backlog"
        workItems={items}
        onWorkItemUpdate={() => {}}
        pageSize={5}
      />,
    );

    // First page: only the first 5 cards are rendered.
    expect(screen.getByText("Item 0")).toBeTruthy();
    expect(screen.getByText("Item 4")).toBeTruthy();
    expect(screen.queryByText("Item 5")).toBeFalsy();

    const loadMore = screen.getByRole("button", { name: "Load more" });
    fireEvent.click(loadMore);

    // Second page reveals the next 5.
    expect(screen.getByText("Item 9")).toBeTruthy();
    expect(screen.queryByText("Item 10")).toBeFalsy();
    expect(screen.getByRole("button", { name: "Load more" })).toBeTruthy();

    // Final click reveals the remainder and hides the button.
    fireEvent.click(screen.getByRole("button", { name: "Load more" }));
    expect(screen.getByText("Item 11")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Load more" })).toBeFalsy();
  });

  test("hides Load more when item count is at or below pageSize", () => {
    const items = Array.from({ length: 5 }, (_, i) =>
      makeItem({ id: String(i), title: `Item ${i}` }),
    );
    render(
      <TaskboardColumn
        status={WorkItemStatus.Done}
        label="Done"
        workItems={items}
        onWorkItemUpdate={() => {}}
        pageSize={5}
      />,
    );

    expect(screen.getByText("Item 4")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Load more" })).toBeFalsy();
  });

  // Moving a Ready item that never started back to Backlog is a legal reset for
  // re-planning. The board is not the thing blocking it — it posts the move like
  // any other — so this pins the request the column already sends and guards the
  // path while the API side is fixed.
  test("dropping a Ready work item into the Backlog column transitions it to Backlog", async () => {
    const transitionSpy = vi
      .spyOn(authServices.workItemService, "transition")
      .mockResolvedValue(undefined as unknown as void);
    const moved = makeItem({ id: "1", status: WorkItemStatus.Backlog });
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(moved);

    const onWorkItemUpdate = vi.fn();
    const onError = vi.fn();
    // The dragged card lives in the Ready column, so the Backlog column it is
    // dropped on does not hold it.
    render(
      <TaskboardColumn
        status={WorkItemStatus.Backlog}
        label="Backlog"
        workItems={[]}
        onWorkItemUpdate={onWorkItemUpdate}
        onError={onError}
      />,
    );

    const column = screen.getByText("Backlog").closest("div");
    fireEvent.drop(column!, {
      dataTransfer: { getData: () => "1" },
    } as unknown as DragEvent);

    await waitFor(() => expect(transitionSpy).toHaveBeenCalledWith("1", WorkItemStatus.Backlog));
    await waitFor(() => expect(onWorkItemUpdate).toHaveBeenCalledWith(moved));
    expect(onError).not.toHaveBeenCalled();
  });

  test("does not call transition when dropping work item into the same column", () => {
    const transitionSpy = vi
      .spyOn(authServices.workItemService, "transition")
      .mockResolvedValue(undefined as unknown as void);

    const item = makeItem({ id: "1", status: WorkItemStatus.Backlog });
    render(
      <TaskboardColumn
        status={WorkItemStatus.Backlog}
        label="Backlog"
        workItems={[item]}
        onWorkItemUpdate={() => {}}
      />,
    );

    const column = screen.getByText("Backlog").closest("div");
    expect(column).toBeTruthy();

    // Simulate dropping the same work item into its own column
    fireEvent.drop(column!, {
      dataTransfer: { getData: () => "1" },
    } as unknown as DragEvent);

    // transition should NOT have been called
    expect(transitionSpy).not.toHaveBeenCalled();
  });
});

describe("WorkItemCard", () => {
  test("shows human feedback badge when reason is set", () => {
    const item = makeItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "PR Awaiting Merge",
    });
    render(<WorkItemCard workItem={item} />);

    expect(screen.getByText("PR Awaiting Merge")).toBeTruthy();
  });

  test("hides badge when no reason is set", () => {
    const item = makeItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: null,
    });
    render(<WorkItemCard workItem={item} />);

    expect(screen.queryByText("PR Awaiting Merge")).toBeFalsy();
    expect(screen.queryByText("Node Failed")).toBeFalsy();
    expect(screen.queryByText("Rebase Conflict")).toBeFalsy();
    expect(screen.queryByText("Human Input Needed")).toBeFalsy();
  });

  test("displays correct badge for each reason type", () => {
    const reasons = ["Node Failed", "Rebase Conflict", "Human Input Needed"];

    for (const reason of reasons) {
      const item = makeItem({
        id: reason,
        status: WorkItemStatus.HumanFeedback,
        humanFeedbackReason: reason,
      });
      render(<WorkItemCard workItem={item} />);
      expect(screen.getByText(reason)).toBeTruthy();
    }
  });
});
