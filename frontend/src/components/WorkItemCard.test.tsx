import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, act } from "@testing-library/react";
import WorkItemCard from "./WorkItemCard";
import { WorkItem, WorkItemPriority, WorkItemStatus } from "../types";

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

function makeItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "1",
    title: "Item A",
    description: "desc",
    status: WorkItemStatus.Running,
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
    currentNodeLabel: null,
    dependencyIds: [],
    dependentIds: [],
    ...overrides,
  };
}

describe("WorkItemCard", () => {
  test("shows the current step and elapsed time for a running item", () => {
    const base = new Date("2026-06-14T12:00:00Z").getTime();
    vi.useFakeTimers();
    vi.setSystemTime(base);

    render(
      <WorkItemCard
        workItem={makeItem({
          status: WorkItemStatus.Running,
          currentNodeLabel: "implement-change",
          // Started 90 seconds ago.
          startedAt: new Date(base - 90_000).toISOString(),
        })}
      />,
    );

    expect(screen.getByText("implement-change")).toBeTruthy();
    expect(screen.getByText("1m 30s")).toBeTruthy();
  });

  test("ticks the elapsed time forward while running", () => {
    const base = new Date("2026-06-14T12:00:00Z").getTime();
    vi.useFakeTimers();
    vi.setSystemTime(base);

    render(
      <WorkItemCard
        workItem={makeItem({
          status: WorkItemStatus.Running,
          startedAt: new Date(base - 90_000).toISOString(),
        })}
      />,
    );

    expect(screen.getByText("1m 30s")).toBeTruthy();

    // advanceTimersByTime also moves the mocked clock forward, so the next
    // interval tick reads a Date.now() one second later.
    act(() => {
      vi.advanceTimersByTime(1_000);
    });

    expect(screen.getByText("1m 31s")).toBeTruthy();
  });

  test("does not show running details for items that are not running", () => {
    render(
      <WorkItemCard
        workItem={makeItem({
          status: WorkItemStatus.Backlog,
          currentNodeLabel: "implement-change",
          startedAt: new Date().toISOString(),
        })}
      />,
    );

    expect(screen.queryByText("implement-change")).toBeNull();
  });

  test("marks tags that name a loop template, leaving free-form tags plain", () => {
    render(
      <WorkItemCard
        workItem={makeItem({ status: WorkItemStatus.Backlog, tags: ["Bug Fix", "urgent"] })}
        loopTemplateNames={["Bug Fix"]}
      />,
    );

    const loopTag = screen.getByText("Bug Fix");
    const plainTag = screen.getByText("urgent");
    expect(loopTag.className).toContain("work-item-tag--loop");
    expect(plainTag.className).not.toContain("work-item-tag--loop");
  });

  test("matches loop tags case-insensitively and leaves tags plain without templates", () => {
    const { rerender } = render(
      <WorkItemCard
        workItem={makeItem({ status: WorkItemStatus.Backlog, tags: ["bug fix"] })}
        loopTemplateNames={["Bug Fix"]}
      />,
    );
    expect(screen.getByText("bug fix").className).toContain("work-item-tag--loop");

    // With no loop templates known, the same tag renders as a plain label.
    rerender(
      <WorkItemCard
        workItem={makeItem({ status: WorkItemStatus.Backlog, tags: ["bug fix"] })}
        loopTemplateNames={[]}
      />,
    );
    expect(screen.getByText("bug fix").className).not.toContain("work-item-tag--loop");
  });

  test("omits the step when a running item has no current node label", () => {
    const base = new Date("2026-06-14T12:00:00Z").getTime();
    vi.useFakeTimers();
    vi.setSystemTime(base);

    const { container } = render(
      <WorkItemCard
        workItem={makeItem({
          status: WorkItemStatus.Running,
          currentNodeLabel: null,
          startedAt: new Date(base - 5_000).toISOString(),
        })}
      />,
    );

    // Elapsed time still shows, but there is no step pill.
    expect(screen.getByText("5s")).toBeTruthy();
    expect(container.querySelector(".work-item-step")).toBeNull();
  });
});
