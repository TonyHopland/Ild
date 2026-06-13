import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import HaltSteerControls from "./HaltSteerControls";
import {
  WorkItemStatus,
  LoopRun,
  LoopRunNode,
  LoopRunStatus,
  LoopRunNodeStatus,
} from "../../types";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function node(overrides: Partial<LoopRunNode> = {}): LoopRunNode {
  return {
    id: "rn-1",
    nodeId: "n-1",
    nodeLabel: "Coder",
    status: LoopRunNodeStatus.Running,
    effectiveInput: null,
    output: null,
    error: null,
    startedAt: "2025-01-01T00:00:00Z",
    completedAt: null,
    executionCount: 1,
    nodeType: "AI",
    ...overrides,
  };
}

function run(overrides: Partial<LoopRun> = {}): LoopRun {
  return {
    id: "run-1",
    workItemId: "wi-1",
    loopTemplateId: "tmpl-1",
    templateVersion: 1,
    status: LoopRunStatus.Running,
    currentNodeId: "n-1",
    isPaused: false,
    nodeExecutionCount: 1,
    startedAt: "2025-01-01T00:00:00Z",
    completedAt: null,
    nodes: [node()],
    ...overrides,
  };
}

describe("HaltSteerControls", () => {
  test("shows the Halt button while an AI node is running", () => {
    const onHalt = vi.fn();
    render(
      <HaltSteerControls run={run()} workItemStatus={WorkItemStatus.Running} onHalt={onHalt} />,
    );
    const btn = screen.getByRole("button", { name: /halt ai node/i });
    fireEvent.click(btn);
    expect(onHalt).toHaveBeenCalledTimes(1);
  });

  test("does not show Halt when the running node is not an AI node", () => {
    render(
      <HaltSteerControls
        run={run({ nodes: [node({ nodeType: "Cmd" })] })}
        workItemStatus={WorkItemStatus.Running}
        onHalt={vi.fn()}
      />,
    );
    expect(screen.queryByRole("button", { name: /halt/i })).toBeNull();
  });

  test("renders the steer window with a note when the run is halted", () => {
    const onResumeSteer = vi.fn();
    render(
      <HaltSteerControls
        run={run({
          status: LoopRunStatus.WaitingHuman,
          isHalted: true,
          nodes: [node({ status: LoopRunNodeStatus.Interrupted })],
        })}
        workItemStatus={WorkItemStatus.HumanFeedback}
        onResumeSteer={onResumeSteer}
      />,
    );
    const textarea = screen.getByPlaceholderText(/optional guidance/i);
    fireEvent.change(textarea, { target: { value: "focus on the bug" } });
    fireEvent.click(screen.getByRole("button", { name: /resume/i }));
    expect(onResumeSteer).toHaveBeenCalledWith("focus on the bug");
  });

  test("renders nothing for a completed run", () => {
    const { container } = render(
      <HaltSteerControls
        run={run({ status: LoopRunStatus.Completed })}
        workItemStatus={WorkItemStatus.Done}
        onHalt={vi.fn()}
        onResumeSteer={vi.fn()}
      />,
    );
    expect(container.innerHTML).toBe("");
  });
});
