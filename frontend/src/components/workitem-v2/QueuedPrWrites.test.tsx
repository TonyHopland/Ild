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

  test("a refused drop says so, and re-reads the run rather than leaving the list as it was", async () => {
    // The usual reason is that the round reached the PR node and sent the lot.
    // Staying silent would leave this panel offering to stop comments that are
    // already public — and offering it in a way that looks like it worked.
    vi.spyOn(loopRunService, "dropQueuedPrWrite").mockRejectedValue({
      message: "No queued write with id w1.",
    });
    const refresh = vi.fn().mockResolvedValue(undefined);
    render(<QueuedPrWrites workItem={workItem()} detail={detail([queued()], refresh)} />);

    fireEvent.click(screen.getByRole("button", { name: "Drop" }));

    await waitFor(() => expect(screen.getByRole("alert").textContent).toContain("No queued write"));
    expect(refresh).toHaveBeenCalled();
    expect((screen.getByRole("button", { name: "Drop" }) as HTMLButtonElement).disabled).toBe(
      false,
    );
  });

  test("the refusal outlives the list it came from", async () => {
    // The re-read that follows a refusal is exactly what empties the queue, so
    // returning early on an empty one would swallow the message that says the
    // comment has already gone out.
    vi.spyOn(loopRunService, "dropQueuedPrWrite").mockRejectedValue({ message: "Already sent." });
    const view = render(<QueuedPrWrites workItem={workItem()} detail={detail([queued()])} />);

    fireEvent.click(screen.getByRole("button", { name: "Drop" }));
    await waitFor(() => expect(screen.getByRole("alert").textContent).toContain("Already sent."));

    view.rerender(<QueuedPrWrites workItem={workItem()} detail={detail([])} />);

    expect(screen.getByRole("alert").textContent).toContain("Already sent.");
    expect(screen.queryByText(/Waiting to go out/)).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));
    expect(view.container.firstChild).toBeNull();
  });

  test("a drop the server took but a re-read that failed still warns the list is stale", async () => {
    vi.spyOn(loopRunService, "dropQueuedPrWrite").mockResolvedValue(undefined);
    const refresh = vi.fn().mockRejectedValue(new Error("network"));
    render(<QueuedPrWrites workItem={workItem()} detail={detail([queued()], refresh)} />);

    fireEvent.click(screen.getByRole("button", { name: "Drop" }));

    await waitFor(() => expect(screen.getByRole("alert").textContent).toContain("out of date"));
  });

  test("a drop in flight disables every other drop, not just its own", async () => {
    // Not what makes concurrent drops safe — same-tick clicks all see the list
    // from before any of them, and the store's compare-and-set is what stops the
    // loser reinstating a comment a human stopped. This is so that a second drop
    // a person starts while watching the first cannot be one of them.
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

  test("names a general comment for what it is, not as a reply", () => {
    // It answers nothing and has no target. Called a reply — as it was while
    // every comment was an answer to something — the one write a person is most
    // likely to want to stop is the one the panel describes wrongly.
    render(
      <QueuedPrWrites
        workItem={workItem()}
        detail={detail([
          queued({
            kind: "comment",
            targetId: "",
            body: "Rebased onto main and re-ran the gate.",
            path: null,
            line: null,
          }),
        ])}
      />,
    );

    expect(screen.getByText(/Comment on the pull request/)).toBeTruthy();
    expect(screen.queryByText(/^Reply/)).toBeNull();
    expect(screen.getByText("Rebased onto main and re-ran the gate.")).toBeTruthy();
  });

  test("drops a queued general comment like any other write", async () => {
    const drop = vi.spyOn(loopRunService, "dropQueuedPrWrite").mockResolvedValue(undefined);
    const refresh = vi.fn().mockResolvedValue(undefined);
    render(
      <QueuedPrWrites
        workItem={workItem()}
        detail={detail(
          [queued({ id: "wc", kind: "comment", targetId: "", path: null, line: null })],
          refresh,
        )}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Drop" }));

    await waitFor(() => expect(drop).toHaveBeenCalledWith("run-1", "wc"));
    await waitFor(() => expect(refresh).toHaveBeenCalled());
  });
});
