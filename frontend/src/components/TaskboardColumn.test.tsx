import { describe, expect, test } from "vite-plus/test";
import { render, screen } from "@testing-library/react";
import TaskboardColumn from "./TaskboardColumn";
import { WorkItem, WorkItemPriority, WorkItemStatus } from "../types";

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
    pullRequestUrl: null,
    pullRequestBranch: null,
    createdAt: new Date().toISOString(),
    startedAt: null,
    completedAt: null,
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
