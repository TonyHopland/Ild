import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor, within, act } from "@testing-library/react";
import RunsPanel from "./RunsPanel";
import { loopRunService } from "../../services/auth";
import {
  WorkItem,
  WorkItemStatus,
  WorkItemPriority,
  LoopRun,
  LoopRunStatus,
  LoopRunNodeStatus,
} from "../../types";

vi.mock("react-router", () => ({
  Link: ({ children, ...rest }: { children: React.ReactNode }) => <a {...rest}>{children}</a>,
}));

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function workItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Test",
    description: "",
    status: WorkItemStatus.Backlog,
    priority: WorkItemPriority.Medium,
    tags: [],
    conversation: [],
    loopTemplateId: "tmpl-1",
    loopTemplateVersion: "v1",
    repositoryId: "repo-1",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: null,
    humanFeedbackActions: null,
    createdAt: "2025-01-01T00:00:00Z",
    startedAt: null,
    completedAt: null,
    currentLoopRunId: null,
    worktreePath: null,
    dependencyIds: [],
    dependentIds: [],
    ...overrides,
  };
}

function run(overrides: Partial<LoopRun> = {}): LoopRun {
  return {
    id: "run-1",
    workItemId: "wi-1",
    loopTemplateId: "tmpl-1",
    templateVersion: 1,
    status: LoopRunStatus.Completed,
    currentNodeId: null,
    isPaused: false,
    nodeExecutionCount: 2,
    hasLocalGitState: true,
    startedAt: "2025-01-01T00:00:00Z",
    completedAt: "2025-01-01T01:00:00Z",
    nodes: [],
    ...overrides,
  };
}

function renderPanel(detail: LoopRun, onReclaimRun = vi.fn().mockResolvedValue(undefined)) {
  const getById = vi.spyOn(loopRunService, "getById").mockResolvedValue(detail);
  const onRunsChanged = vi.fn();
  render(
    <RunsPanel
      workItem={workItem()}
      runs={[detail]}
      progressText=""
      onRunsChanged={onRunsChanged}
      onReclaimRun={onReclaimRun}
    />,
  );
  return { getById, onReclaimRun, onRunsChanged };
}

const cleanUpButton = () => screen.queryByRole("button", { name: /clean up worktree/i });

describe("RunsPanel cleanup action", () => {
  test("confirms, then frees the run's worktree and branch and refetches", async () => {
    const { onReclaimRun, onRunsChanged, getById } = renderPanel(run());

    await waitFor(() => expect(cleanUpButton()).not.toBeNull());
    fireEvent.click(cleanUpButton()!);

    // The action is destructive on disk, so nothing happens before confirming.
    expect(onReclaimRun).not.toHaveBeenCalled();
    getById.mockResolvedValue(run({ hasLocalGitState: false }));
    fireEvent.click(screen.getByRole("button", { name: /confirm clean up/i }));

    await waitFor(() => expect(onReclaimRun).toHaveBeenCalledWith("run-1"));
    await waitFor(() => expect(onRunsChanged).toHaveBeenCalled());
    // Once reclaimed there is nothing left to reclaim.
    await waitFor(() => expect(cleanUpButton()).toBeNull());
  });

  test("shows why a refused cleanup did not happen", async () => {
    const onReclaimRun = vi.fn().mockRejectedValue(new Error("Could not reclaim the worktree"));
    renderPanel(run(), onReclaimRun);

    await waitFor(() => expect(cleanUpButton()).not.toBeNull());
    fireEvent.click(cleanUpButton()!);
    fireEvent.click(screen.getByRole("button", { name: /confirm clean up/i }));

    await waitFor(() =>
      expect(screen.getByRole("alert").textContent).toContain("Could not reclaim the worktree"),
    );
  });

  test("is not offered for a run that is still live", async () => {
    renderPanel(run({ status: LoopRunStatus.WaitingHuman, completedAt: null }));
    await waitFor(() => expect(screen.getByText(/waitinghuman/i)).not.toBeNull());
    expect(cleanUpButton()).toBeNull();
  });

  test("is not offered once the run holds no local git state", async () => {
    renderPanel(run({ hasLocalGitState: false }));
    await waitFor(() => expect(screen.getByText(/open full run view/i)).not.toBeNull());
    expect(cleanUpButton()).toBeNull();
  });
});

const RUN_A = "aaaaaaaa-1111-4111-8111-111111111111";
const RUN_B = "bbbbbbbb-2222-4222-8222-222222222222";
const RUN_C = "cccccccc-3333-4333-8333-333333333333";

function runWithNode(id: string, overrides: Partial<LoopRun> = {}): LoopRun {
  return run({
    id,
    nodes: [
      {
        id: `rn-${id}`,
        nodeId: "n-1",
        nodeLabel: `Node of ${id.slice(0, 8)}`,
        status: LoopRunNodeStatus.Succeeded,
        effectiveInput: null,
        output: null,
        error: null,
        startedAt: "2025-01-01T00:00:00Z",
        completedAt: "2025-01-01T00:10:00Z",
        executionCount: 1,
      },
    ],
    ...overrides,
  });
}

function actions() {
  return {
    onPauseRun: vi.fn().mockResolvedValue(undefined),
    onResumeRun: vi.fn().mockResolvedValue(undefined),
    onCancelRun: vi.fn().mockResolvedValue(undefined),
    onDeleteRun: vi.fn().mockResolvedValue(undefined),
  };
}

// The server's view of each run; tests mutate it to model what an action did.
function renderActionPanel(
  runs: LoopRun[],
  opts: {
    workItem?: WorkItem;
    handlers?: Partial<ReturnType<typeof actions>>;
  } = {},
) {
  const server = new Map(runs.map((r) => [r.id, r]));
  const getById = vi
    .spyOn(loopRunService, "getById")
    .mockImplementation(async (id: string) => server.get(id)!);
  const handlers = { ...actions(), ...opts.handlers };
  const onRunsChanged = vi.fn();
  render(
    <RunsPanel
      workItem={opts.workItem ?? workItem()}
      runs={runs}
      progressText=""
      onRunsChanged={onRunsChanged}
      {...handlers}
    />,
  );
  return { server, getById, handlers, onRunsChanged };
}

const actionButton = (name: RegExp) => screen.queryByRole("button", { name });
const PAUSE = /pause run/i;
const RESUME = /resume run/i;
const CANCEL_RUN = /cancel run/i;
const DELETE_RUN = /delete run/i;
const runEntry = (id: string) => screen.queryByRole("button", { name: new RegExp(id.slice(0, 8)) });
const isDisabled = (el: HTMLElement | null) => (el as HTMLButtonElement | null)?.disabled;

function deferred<T = void>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

async function confirmDelete() {
  fireEvent.click(actionButton(DELETE_RUN)!);
  const group = await screen.findByRole("group", { name: /delete/i });
  const confirm = within(group)
    .getAllByRole("button")
    .find((b) => !/^\s*cancel\s*$/i.test(b.textContent ?? ""))!;
  fireEvent.click(confirm);
}

describe("RunsPanel run list", () => {
  test("each run entry shows the run's short id", async () => {
    renderActionPanel([runWithNode(RUN_A), runWithNode(RUN_B)]);
    await screen.findByText("Node of aaaaaaaa");

    expect(runEntry(RUN_A)).not.toBeNull();
    expect(runEntry(RUN_B)).not.toBeNull();
    expect(screen.queryByText(new RegExp(RUN_A.slice(8)))).toBeNull();
  });
});

describe("RunsPanel run actions", () => {
  test.each([
    [
      "Running",
      LoopRunStatus.Running,
      false,
      { pause: true, resume: false, cancel: true, del: false },
    ],
    [
      "Running + paused",
      LoopRunStatus.Running,
      true,
      { pause: false, resume: true, cancel: true, del: false },
    ],
    [
      "Completed",
      LoopRunStatus.Completed,
      false,
      { pause: false, resume: false, cancel: false, del: true },
    ],
    [
      "Failed",
      LoopRunStatus.Failed,
      false,
      { pause: false, resume: false, cancel: false, del: true },
    ],
    [
      "Cancelled",
      LoopRunStatus.Cancelled,
      false,
      { pause: false, resume: false, cancel: false, del: true },
    ],
    [
      "WaitingHuman",
      LoopRunStatus.WaitingHuman,
      false,
      { pause: false, resume: false, cancel: false, del: true },
    ],
  ])("%s run offers exactly its allowed actions", async (_label, status, isPaused, expected) => {
    renderActionPanel([
      runWithNode(RUN_A, {
        status,
        isPaused,
        completedAt: status === LoopRunStatus.Running ? null : "2025-01-01T01:00:00Z",
      }),
    ]);
    await screen.findByText("Node of aaaaaaaa");

    expect(actionButton(PAUSE) !== null).toBe(expected.pause);
    expect(actionButton(RESUME) !== null).toBe(expected.resume);
    expect(actionButton(CANCEL_RUN) !== null).toBe(expected.cancel);
    expect(actionButton(DELETE_RUN) !== null).toBe(expected.del);
  });

  test.each([
    ["pause", false, PAUSE, "onPauseRun", { isPaused: true }],
    ["resume", true, RESUME, "onResumeRun", { isPaused: false }],
    ["cancel", false, CANCEL_RUN, "onCancelRun", { status: LoopRunStatus.Cancelled }],
  ] as const)(
    "%s calls its handler for the shown run, refreshes and shows the new state",
    async (_label, isPaused, name, handler, after) => {
      const { server, handlers, onRunsChanged } = renderActionPanel(
        [
          runWithNode(RUN_B, { status: LoopRunStatus.Completed }),
          runWithNode(RUN_A, { status: LoopRunStatus.Running, isPaused, completedAt: null }),
        ],
        { workItem: workItem({ currentLoopRunId: RUN_A }) },
      );
      await screen.findByText("Node of aaaaaaaa");

      handlers[handler].mockImplementation(async () => {
        server.set(RUN_A, { ...server.get(RUN_A)!, ...after });
      });
      fireEvent.click(actionButton(name)!);

      await waitFor(() => expect(handlers[handler]).toHaveBeenCalledWith(RUN_A));
      await waitFor(() => expect(onRunsChanged).toHaveBeenCalled());
      if (handler === "onPauseRun") {
        await waitFor(() => expect(screen.getByText(/\(paused\)/i)).not.toBeNull());
        expect(actionButton(RESUME)).not.toBeNull();
      } else if (handler === "onResumeRun") {
        await waitFor(() => expect(screen.queryByText(/\(paused\)/i)).toBeNull());
        expect(actionButton(PAUSE)).not.toBeNull();
      } else {
        await waitFor(() => expect(actionButton(CANCEL_RUN)).toBeNull());
        expect(actionButton(DELETE_RUN)).not.toBeNull();
      }
      for (const other of ["onPauseRun", "onResumeRun", "onCancelRun", "onDeleteRun"] as const) {
        if (other !== handler) expect(handlers[other]).not.toHaveBeenCalled();
      }
    },
  );

  test("delete asks first and dismissing the confirmation sends nothing", async () => {
    const { handlers } = renderActionPanel([runWithNode(RUN_A)]);
    await screen.findByText("Node of aaaaaaaa");

    fireEvent.click(actionButton(DELETE_RUN)!);
    expect(handlers.onDeleteRun).not.toHaveBeenCalled();
    const group = await screen.findByRole("group", { name: /delete/i });
    fireEvent.click(within(group).getByRole("button", { name: /^cancel$/i }));

    expect(screen.queryByRole("group", { name: /delete/i })).toBeNull();
    expect(handlers.onDeleteRun).not.toHaveBeenCalled();
    expect(runEntry(RUN_A)).not.toBeNull();
  });

  test("a deleted run leaves the list at once and the selection moves to the current run", async () => {
    // The parent's `runs` is never refetched here, so the run must go from the
    // list without waiting for it.
    const { handlers, onRunsChanged } = renderActionPanel(
      [
        runWithNode(RUN_A),
        runWithNode(RUN_B, { status: LoopRunStatus.Failed }),
        runWithNode(RUN_C, { status: LoopRunStatus.Cancelled }),
      ],
      { workItem: workItem({ currentLoopRunId: RUN_C }) },
    );
    await screen.findByText("Node of cccccccc");
    fireEvent.click(runEntry(RUN_A)!);
    await screen.findByText("Node of aaaaaaaa");

    await confirmDelete();

    await waitFor(() => expect(handlers.onDeleteRun).toHaveBeenCalledWith(RUN_A));
    await waitFor(() => expect(runEntry(RUN_A)).toBeNull());
    await screen.findByText("Node of cccccccc");
    expect(screen.queryByText("Node of aaaaaaaa")).toBeNull();
    expect(runEntry(RUN_B)).not.toBeNull();
    expect(onRunsChanged).toHaveBeenCalled();
  });

  test("deleting the current run moves the selection to the first remaining run", async () => {
    const { handlers } = renderActionPanel(
      [
        runWithNode(RUN_A),
        runWithNode(RUN_B, { status: LoopRunStatus.Failed }),
        runWithNode(RUN_C),
      ],
      { workItem: workItem({ currentLoopRunId: RUN_A }) },
    );
    await screen.findByText("Node of aaaaaaaa");

    await confirmDelete();

    await waitFor(() => expect(handlers.onDeleteRun).toHaveBeenCalledWith(RUN_A));
    await screen.findByText("Node of bbbbbbbb");
    expect(screen.queryByText("Node of aaaaaaaa")).toBeNull();
    expect(runEntry(RUN_A)).toBeNull();
  });

  test("deleting the only run shows the empty state", async () => {
    renderActionPanel([runWithNode(RUN_A)], { workItem: workItem({ currentLoopRunId: RUN_A }) });
    await screen.findByText("Node of aaaaaaaa");

    await confirmDelete();

    await screen.findByText("No runs yet for this work item.");
    expect(screen.queryByText("Node of aaaaaaaa")).toBeNull();
  });

  // The API client rejects with a plain { status, message } object (services/api.ts),
  // not an Error, so that is the shape the handlers are given here.
  test.each([
    [
      "pause",
      { status: LoopRunStatus.Running, isPaused: false, completedAt: null },
      PAUSE,
      "onPauseRun",
    ],
    [
      "resume",
      { status: LoopRunStatus.Running, isPaused: true, completedAt: null },
      RESUME,
      "onResumeRun",
    ],
    [
      "cancel",
      { status: LoopRunStatus.Running, isPaused: false, completedAt: null },
      CANCEL_RUN,
      "onCancelRun",
    ],
    ["delete (409)", { status: LoopRunStatus.Completed }, DELETE_RUN, "onDeleteRun"],
    ["delete (503)", { status: LoopRunStatus.Failed }, DELETE_RUN, "onDeleteRun"],
    ["delete (400)", { status: LoopRunStatus.Cancelled }, DELETE_RUN, "onDeleteRun"],
  ] as const)(
    "a refused %s shows the server's message and keeps the run",
    async (label, state, name, handler) => {
      const status = Number(/\d{3}/.exec(label)?.[0] ?? 409);
      const message = `Server refused the ${label} of this run`;
      const { handlers } = renderActionPanel([runWithNode(RUN_A, state)], {
        handlers: { [handler]: vi.fn().mockRejectedValue({ status, message }) },
      });
      await screen.findByText("Node of aaaaaaaa");

      if (handler === "onDeleteRun") await confirmDelete();
      else fireEvent.click(actionButton(name)!);

      await waitFor(() => expect(screen.getByRole("alert").textContent).toContain(message));
      expect(handlers[handler]).toHaveBeenCalledWith(RUN_A);
      expect(runEntry(RUN_A)).not.toBeNull();
      expect(screen.getByText("Node of aaaaaaaa")).not.toBeNull();
      const usable = screen.getAllByRole("button", {
        name: handler === "onDeleteRun" ? /delete/i : name,
      });
      expect(usable.length).toBeGreaterThan(0);
      for (const b of usable) expect(isDisabled(b)).toBe(false);

      fireEvent.click(within(screen.getByRole("alert")).getByRole("button"));
      expect(screen.queryByRole("alert")).toBeNull();
    },
  );

  test("an in-flight action blocks only its own run and never overwrites a newly shown run", async () => {
    const pending = deferred();
    const { server, handlers } = renderActionPanel(
      [
        runWithNode(RUN_A, { status: LoopRunStatus.Running, completedAt: null }),
        runWithNode(RUN_B, { status: LoopRunStatus.Running, completedAt: null }),
      ],
      {
        workItem: workItem({ currentLoopRunId: RUN_A }),
        handlers: { onPauseRun: vi.fn(() => pending.promise) },
      },
    );
    await screen.findByText("Node of aaaaaaaa");

    fireEvent.click(actionButton(PAUSE)!);
    await waitFor(() => expect(isDisabled(actionButton(PAUSE))).toBe(true));
    expect(isDisabled(actionButton(CANCEL_RUN))).toBe(true);
    fireEvent.click(actionButton(PAUSE)!);
    expect(handlers.onPauseRun).toHaveBeenCalledTimes(1);

    fireEvent.click(runEntry(RUN_B)!);
    await screen.findByText("Node of bbbbbbbb");
    expect(isDisabled(actionButton(PAUSE))).toBe(false);
    expect(isDisabled(actionButton(CANCEL_RUN))).toBe(false);

    server.set(RUN_A, { ...server.get(RUN_A)!, isPaused: true });
    await act(async () => {
      pending.resolve();
      await pending.promise;
    });

    await waitFor(() => expect(handlers.onPauseRun).toHaveBeenCalledWith(RUN_A));
    expect(screen.getByText("Node of bbbbbbbb")).not.toBeNull();
    expect(screen.queryByText("Node of aaaaaaaa")).toBeNull();
    expect(screen.queryByText(/\(paused\)/i)).toBeNull();
    expect(actionButton(PAUSE)).not.toBeNull();
  });
});
