import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import HaltSteerControls from "./HaltSteerControls";
import { HaltReason, LoopRun, LoopRunStatus, WorkItemStatus } from "../../types";

afterEach(() => {
  cleanup();
});

function parked(haltReason: HaltReason): LoopRun {
  return {
    id: "run-1",
    workItemId: "wi-1",
    loopTemplateId: "tmpl-1",
    templateVersion: 1,
    status: LoopRunStatus.WaitingHuman,
    currentNodeId: "n-1",
    isPaused: false,
    isHalted: true,
    haltReason,
    missingGlobalVariables: ["review_rules"],
    nodeExecutionCount: 1,
    startedAt: "2025-01-01T00:00:00Z",
    completedAt: null,
    nodes: [],
  };
}

function noteBox(haltReason: HaltReason) {
  render(
    <MemoryRouter>
      <HaltSteerControls
        run={parked(haltReason)}
        workItemStatus={WorkItemStatus.HumanFeedback}
        onResumeSteer={vi.fn()}
      />
    </MemoryRouter>,
  );
  return screen.getByRole("textbox") as HTMLTextAreaElement;
}

// A missing-global park can sit before a step with no agent to steer, so its
// note is not promised to one; it is added to the run's conversation.
describe("HaltSteerControls resume note", () => {
  test("is worded for the conversation, not an agent, when globals parked the run", () => {
    const placeholder = noteBox(HaltReason.GlobalVariableMissing).placeholder;
    expect(placeholder).not.toMatch(/agent/i);
    expect(placeholder).toMatch(/conversation/i);
  });

  test("still speaks to the resumed agent for a human halt", () => {
    expect(noteBox(HaltReason.Human).placeholder).toMatch(/resumed agent/i);
  });
});
