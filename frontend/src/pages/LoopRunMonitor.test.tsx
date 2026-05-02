import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, waitFor, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import LoopRunMonitor from "./LoopRunMonitor";
import { LoopRunStatus, LoopRun } from "../types";
import * as authServices from "../services/auth";
import * as signalRHook from "../hooks/useSignalR";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

beforeEach(() => {
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn(),
    connectionState: "connected",
  });
});

function makeRun(overrides: Partial<LoopRun> = {}): LoopRun {
  return {
    id: "run-1",
    workItemId: "wi-1",
    loopTemplateId: "tmpl-1",
    templateVersion: 1,
    status: LoopRunStatus.Running,
    isPaused: false,
    startedAt: "2025-01-01T00:00:00Z",
    completedAt: null,
    currentNodeId: null,
    nodeExecutionCount: 0,
    nodes: [],
    ...overrides,
  };
}

describe("LoopRunMonitor error UX", () => {
  test("shows an error banner when pause fails and dismisses on click", async () => {
    vi.spyOn(authServices.loopRunService, "getAll").mockResolvedValue([makeRun()]);
    vi.spyOn(authServices.loopRunService, "pause").mockRejectedValue(new Error("network down"));

    render(
      <MemoryRouter>
        <LoopRunMonitor />
      </MemoryRouter>,
    );

    const pauseBtn = await screen.findByRole("button", { name: /pause/i });
    fireEvent.click(pauseBtn);

    const alert = await waitFor(() => screen.getByRole("alert"));
    expect(alert.textContent).toContain("network down");

    fireEvent.click(screen.getByRole("button", { name: /dismiss/i }));
    await waitFor(() => {
      expect(screen.queryByRole("alert")).toBeNull();
    });
  });
});
