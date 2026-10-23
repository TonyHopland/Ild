import { afterEach, describe, expect, test } from "vite-plus/test";
import { cleanup, render, screen } from "@testing-library/react";
import TaskboardColumn from "./TaskboardColumn";
import { WorkItem, WorkItemStatus } from "../types";
import { TASKBOARD_COLUMNS } from "../utils/taskboardColumns";

afterEach(cleanup);

const NOTE =
  "Ild starts the top-most item that can start; a waiting item doesn't hold up the ones below it.";

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

function renderColumn(status: WorkItemStatus, label: string, alone: boolean) {
  render(
    <TaskboardColumn
      status={status}
      label={label}
      workItems={[item("a", WorkItemStatus.Ready), item("b", WorkItemStatus.WorkQueue)]}
      total={2}
      onWorkItemUpdate={() => {}}
      alone={alone}
      foldChoice={false}
      onFoldChange={() => {}}
    />,
  );
}

describe("the Work queue column's order note", () => {
  test("a Work queue column with cards offers it from a focusable info icon", () => {
    renderColumn(WorkItemStatus.WorkQueue, "Work queue", false);

    const info = screen.getByRole("img", { name: "How Ild picks from Work queue" });
    expect(info.tabIndex).toBe(0);
    expect(info.getAttribute("title")).toBe(NOTE);
    const describedBy = info.getAttribute("aria-describedby")!;
    expect(document.getElementById(describedBy)?.textContent).toBe(NOTE);
  });

  test("on a phone, where there is no header, the Work queue column spells it out", () => {
    renderColumn(WorkItemStatus.WorkQueue, "Work queue", true);

    expect(screen.getByText(NOTE)).toBeTruthy();
  });

  test.each(
    TASKBOARD_COLUMNS.filter((c) => c.key !== WorkItemStatus.WorkQueue).flatMap((c) => [
      [c.label, c.key, false],
      [c.label, c.key, true],
    ]) as [string, WorkItemStatus, boolean][],
  )("the %s column does not show it (alone: %s)", (label, key, alone) => {
    renderColumn(key, label, alone);

    expect(screen.queryByRole("img", { name: /How Ild picks/ })).toBeNull();
    expect(screen.queryByText(NOTE)).toBeNull();
    expect(document.body.textContent).not.toContain("top-most item");
  });
});
