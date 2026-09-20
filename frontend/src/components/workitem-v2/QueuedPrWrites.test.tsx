import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor } from "@testing-library/react";
import { QueuedPrWrites } from "./panels";
import { loopRunService } from "../../services/auth";
import { WorkItem, WorkItemStatus, WorkItemPriority, PrQueuedWrite } from "../../types";
import type { WorkItemDetail } from "./useWorkItemDetail";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function workItem(): WorkItem {
  return {
    id: "wi-1",
    title: "Test",
    description: "",
    status: WorkItemStatus.Running,
    priority: WorkItemPriority.Medium,
    tags: [],
    conversation: [],
    loopTemplateId: "tmpl-1",
    loopTemplateVersion: "v1",
    repositoryId: "repo-1",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: null,
    currentLoopRunId: "run-1",
  } as unknown as WorkItem;
}

function queued(overrides: Partial<PrQueuedWrite> = {}): PrQueuedWrite {
  return {
    id: "w1",
    kind: "reply",
    targetId: "4049159495",
    body: "That compiles.",
    path: "src/A.cs",
    line: 10,
    queuedAt: "2026-09-20T12:00:00Z",
    ...overrides,
  };
}

function detail(writes: PrQueuedWrite[], refresh = vi.fn().mockResolvedValue(undefined)) {
  return {
    currentRun: { prQueuedWrites: writes },
    refreshCurrentRun: refresh,
  } as unknown as WorkItemDetail;
}

describe("what the round is about to say on the pull request", () => {
  test("says nothing at all when nothing is waiting", () => {
    const { container } = render(<QueuedPrWrites workItem={workItem()} detail={detail([])} />);
    expect(container.firstChild).toBeNull();
  });

  test("shows where each answer would land, and what it says", () => {
    render(
      <QueuedPrWrites
        workItem={workItem()}
        detail={detail([
          queued(),
          queued({ id: "w2", kind: "resolve", body: null, path: "src/B.cs", line: 92 }),
        ])}
      />,
    );

    expect(screen.getByText(/Waiting to go out on the pull request/)).toBeTruthy();
    expect(screen.getByText(/src\/A\.cs:10/)).toBeTruthy();
    expect(screen.getByText("That compiles.")).toBeTruthy();
    expect(screen.getByText(/Resolve thread/)).toBeTruthy();
    expect(screen.getByText(/src\/B\.cs:92/)).toBeTruthy();
  });

  test("dropping one asks the server and then re-reads the run it lives on", async () => {
    const drop = vi.spyOn(loopRunService, "dropQueuedPrWrite").mockResolvedValue(undefined);
    const refresh = vi.fn().mockResolvedValue(undefined);
    render(<QueuedPrWrites workItem={workItem()} detail={detail([queued()], refresh)} />);

    fireEvent.click(screen.getByRole("button", { name: "Drop" }));

    await waitFor(() => expect(drop).toHaveBeenCalledWith("run-1", "w1"));
    await waitFor(() => expect(refresh).toHaveBeenCalled());
  });

  test("a drop in flight disables every other drop, not just its own", async () => {
    // Two drops computed from the same list would each write it back without
    // the other's removal, and the second to land would put back a comment a
    // human had stopped.
    let release: (() => void) | undefined;
    vi.spyOn(loopRunService, "dropQueuedPrWrite").mockReturnValue(
      new Promise<void>((resolve) => {
        release = resolve;
      }),
    );
    render(
      <QueuedPrWrites
        workItem={workItem()}
        detail={detail([queued(), queued({ id: "w2", targetId: "4051372317" })])}
      />,
    );

    const buttons = screen.getAllByRole("button");
    fireEvent.click(buttons[0]);

    await waitFor(() =>
      expect(screen.getAllByRole("button").every((b) => (b as HTMLButtonElement).disabled)).toBe(
        true,
      ),
    );
    release?.();
  });
});
