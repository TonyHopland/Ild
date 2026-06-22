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

  test("abandons a running run after confirming, even on a non-AI node", () => {
    const onCleanupBacklog = vi.fn();
    render(
      <HaltSteerControls
        run={run({ nodes: [node({ nodeType: "Cmd" })] })}
        workItemStatus={WorkItemStatus.Running}
        onCleanupBacklog={onCleanupBacklog}
      />,
    );
    // No Halt button on a non-AI node, but Abandon is still available.
    expect(screen.queryByRole("button", { name: /halt/i })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /abandon run/i }));
    // Click is a two-step confirm: the first click only reveals the prompt.
    expect(onCleanupBacklog).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: /confirm abandon/i }));
    expect(onCleanupBacklog).toHaveBeenCalledTimes(1);
  });

  test("abandon confirm can be cancelled without stopping the run", () => {
    const onCleanupBacklog = vi.fn();
    render(
      <HaltSteerControls
        run={run()}
        workItemStatus={WorkItemStatus.Running}
        onHalt={vi.fn()}
        onCleanupBacklog={onCleanupBacklog}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: /abandon run/i }));
    fireEvent.click(screen.getByRole("button", { name: /^cancel$/i }));
    expect(onCleanupBacklog).not.toHaveBeenCalled();
    // The Abandon button comes back so the run can still be dropped later.
    expect(screen.getByRole("button", { name: /abandon run/i })).toBeTruthy();
  });

  test("abandons a run parked for human feedback while the AI is not running", () => {
    const onCleanupBacklog = vi.fn();
    render(
      <HaltSteerControls
        run={run({
          status: LoopRunStatus.WaitingHuman,
          nodes: [node({ nodeType: "Human", status: LoopRunNodeStatus.WaitingHuman })],
        })}
        workItemStatus={WorkItemStatus.HumanFeedback}
        onCleanupBacklog={onCleanupBacklog}
      />,
    );
    // The run is parked at a human-feedback node (no AI in flight), so there is
    // no Halt button, but Abandon is still offered so the run can be dropped.
    expect(screen.queryByRole("button", { name: /halt/i })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /abandon run/i }));
    fireEvent.click(screen.getByRole("button", { name: /confirm abandon/i }));
    expect(onCleanupBacklog).toHaveBeenCalledTimes(1);
  });

  test("does not duplicate Abandon when the run is halted", () => {
    const onCleanupBacklog = vi.fn();
    render(
      <HaltSteerControls
        run={run({
          status: LoopRunStatus.WaitingHuman,
          isHalted: true,
          nodes: [node({ status: LoopRunNodeStatus.Interrupted })],
        })}
        workItemStatus={WorkItemStatus.HumanFeedback}
        onResumeSteer={vi.fn()}
        onCleanupBacklog={onCleanupBacklog}
      />,
    );
    // A halted run already exposes "Cleanup -> Backlog" in the steer window, so
    // the standalone Abandon affordance must not appear alongside it.
    expect(screen.queryByRole("button", { name: /abandon run/i })).toBeNull();
  });

  test("hides Abandon when showAbandon is false but keeps Halt", () => {
    const onCleanupBacklog = vi.fn();
    render(
      <HaltSteerControls
        run={run()}
        workItemStatus={WorkItemStatus.Running}
        onHalt={vi.fn()}
        onCleanupBacklog={onCleanupBacklog}
        showAbandon={false}
      />,
    );
    // The action tab opts out of Abandon, but the live Halt control remains.
    expect(screen.queryByRole("button", { name: /abandon run/i })).toBeNull();
    expect(screen.getByRole("button", { name: /halt ai node/i })).toBeTruthy();
  });

  test("renders nothing when showAbandon is false and only Abandon would apply", () => {
    const { container } = render(
      <HaltSteerControls
        run={run({
          status: LoopRunStatus.WaitingHuman,
          nodes: [node({ nodeType: "Human", status: LoopRunNodeStatus.WaitingHuman })],
        })}
        workItemStatus={WorkItemStatus.HumanFeedback}
        onCleanupBacklog={vi.fn()}
        showAbandon={false}
      />,
    );
    // No AI node in flight and Abandon suppressed, so there is nothing to show.
    expect(container.innerHTML).toBe("");
  });

  test("showAbandon false still leaves the halted steer window's Cleanup -> Backlog", () => {
    const onCleanupBacklog = vi.fn();
    render(
      <HaltSteerControls
        run={run({
          status: LoopRunStatus.WaitingHuman,
          isHalted: true,
          nodes: [node({ status: LoopRunNodeStatus.Interrupted })],
        })}
        workItemStatus={WorkItemStatus.HumanFeedback}
        onResumeSteer={vi.fn()}
        onCleanupBacklog={onCleanupBacklog}
        showAbandon={false}
      />,
    );
    // The standalone Abandon is gone, but the halted run's own wind-down stays.
    expect(screen.queryByRole("button", { name: /abandon run/i })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /cleanup -> backlog/i }));
    expect(onCleanupBacklog).toHaveBeenCalledTimes(1);
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
