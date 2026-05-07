import { afterEach, describe, expect, test } from "vite-plus/test";
import { render, screen, cleanup } from "@testing-library/react";
import TaskboardColumn from "./TaskboardColumn";
import WorkItemCard from "./WorkItemCard";
import { WorkItem, WorkItemPriority, WorkItemStatus } from "../types";

afterEach(() => {
  cleanup();
});

function makeItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "1",
    title: "Item A",
    description: "desc",
    status: WorkItemStatus.Backlog,
    priority: WorkItemPriority.Medium,
    labels: [],
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
