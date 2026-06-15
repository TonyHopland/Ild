import { afterEach, describe, expect, test } from "vite-plus/test";
import { render, screen, cleanup } from "@testing-library/react";
import RunCostSummary from "./RunCostSummary";
import { LoopRun, LoopRunStatus } from "../../types";

afterEach(() => {
  cleanup();
});

function makeRun(overrides: Partial<LoopRun> = {}): LoopRun {
  return {
    id: "run-1",
    workItemId: "wi-1",
    loopTemplateId: "t-1",
    templateVersion: 1,
    status: LoopRunStatus.Completed,
    currentNodeId: null,
    isPaused: false,
    nodeExecutionCount: 0,
    startedAt: "2026-06-01T00:00:00Z",
    completedAt: "2026-06-01T00:05:00Z",
    nodes: [],
    ...overrides,
  };
}

describe("RunCostSummary", () => {
  test("renders cost and token totals for a run with usage", () => {
    render(
      <RunCostSummary
        run={makeRun({ totalInputTokens: 12_300, totalOutputTokens: 4_000, totalCostUsd: 1.25 })}
      />,
    );

    expect(screen.getByText("$1.25")).toBeTruthy();
    expect(screen.getByText("12.3k")).toBeTruthy();
    expect(screen.getByText("4.0k")).toBeTruthy();
  });

  test("renders tokens without a cost figure when cost was not reported", () => {
    render(
      <RunCostSummary
        run={makeRun({ totalInputTokens: 50, totalOutputTokens: 10, totalCostUsd: null })}
      />,
    );

    expect(screen.queryByText("cost")).toBeNull();
    expect(screen.getByText("50")).toBeTruthy();
  });

  test("renders nothing when the run has no token or cost data", () => {
    const { container } = render(<RunCostSummary run={makeRun()} />);
    expect(container.firstChild).toBeNull();
  });
});
