import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, act, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import WorkItemModalV2 from "./WorkItemModalV2";
import {
  WorkItem,
  WorkItemStatus,
  WorkItemPriority,
  LoopRun,
  LoopRunStatus,
  LoopRunNodeStatus,
  LoopRunNode,
  LoopRunVariableWrite,
  RemotePrSnapshot,
} from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

function makeWorkItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Test Work Item",
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
    humanFeedbackActions: null,
    createdAt: "2026-09-24T09:00:00Z",
    startedAt: null,
    completedAt: null,
    currentLoopRunId: "run-1",
    dependencyIds: [],
    dependentIds: [],
    ...overrides,
  };
}

function execution(id: string, label: string, startedAt: string, completedAt: string): LoopRunNode {
  return {
    id,
    nodeId: `tpl-${label}`,
    nodeLabel: label,
    status: LoopRunNodeStatus.Succeeded,
    effectiveInput: null,
    output: null,
    error: null,
    startedAt,
    completedAt,
    executionCount: 1,
  };
}

function makeRun(overrides: Partial<LoopRun> = {}): LoopRun {
  return {
    id: "run-1",
    workItemId: "wi-1",
    loopTemplateId: "tmpl-1",
    templateVersion: 1,
    status: LoopRunStatus.WaitingHuman,
    currentNodeId: null,
    isPaused: false,
    nodeExecutionCount: 0,
    startedAt: "2026-09-24T09:00:00Z",
    completedAt: null,
    nodes: [],
    ...overrides,
  };
}

function snapshot(overrides: Partial<RemotePrSnapshot> = {}): RemotePrSnapshot {
  return {
    title: "Debounce the search box",
    body: null,
    state: "open",
    merged: false,
    mergeable: true,
    mergeableState: "clean",
    ci: "Passed",
    approved: false,
    changesRequested: true,
    conversation: [
      {
        kind: "review_comment",
        author: "tony",
        body: "250 ms feels sluggish.",
        createdAt: "2026-09-24T10:00:00Z",
        state: null,
      },
    ],
    fetchedAt: "2026-09-24T10:01:00Z",
    ...overrides,
  };
}

function mockServices(run: LoopRun, writes: LoopRunVariableWrite[] = []) {
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn(),
    connectionState: "connected",
  } as unknown as ReturnType<typeof signalRHook.useSignalR>);
  vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([run]);
  vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.loopRunService, "getById").mockResolvedValue(run);
  vi.spyOn(authServices.workItemService, "getVariableWrites").mockResolvedValue(writes);
  vi.spyOn(authServices.loopRunService, "getEvents").mockResolvedValue({
    entries: [],
    nextCursor: 0,
  } as unknown as Awaited<ReturnType<typeof authServices.loopRunService.getEvents>>);
}

async function openActionTab(workItem: WorkItem) {
  await act(async () => {
    render(
      <MemoryRouter>
        <WorkItemModalV2 workItem={workItem} onClose={vi.fn()} onSave={vi.fn()} />
      </MemoryRouter>,
    );
    await Promise.resolve();
  });
  await act(async () => {
    fireEvent.click(screen.getByRole("tab", { name: /Action/ }));
    await Promise.resolve();
  });
  return document.getElementById("wiv2-panel-action") as HTMLElement;
}

const row = (el: HTMLElement) => el.closest(".wiv2-bubble-row") as HTMLElement;

describe("the Action tab thread", () => {
  test("AI turns sit on the AI side and human replies on the human side, oldest first", async () => {
    mockServices(makeRun());
    const panel = await openActionTab(
      makeWorkItem({
        conversation: [
          { role: "ai", content: "first turn", timestamp: "2026-09-24T09:10:00Z", name: "Coder" },
          { role: "human", content: "my reply", timestamp: "2026-09-24T09:20:00Z", name: null },
          { role: "ai", content: "second turn", timestamp: "2026-09-24T09:30:00Z", name: "Coder" },
        ],
      }),
    );

    const turns = ["first turn", "my reply", "second turn"].map((t) =>
      row(within(panel).getByText(t)),
    );
    expect(turns.map((r) => r.classList.contains("wiv2-bubble-row-human"))).toEqual([
      false,
      true,
      false,
    ]);
    expect(
      turns[0].compareDocumentPosition(turns[2]) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    // An AI turn is labelled with its author; a human reply carries only its time.
    expect(within(turns[0]).getByText("Coder")).toBeTruthy();
    expect(within(turns[1]).queryByText("You")).toBeNull();
  });

  test("the live run is shown in an AI bubble", async () => {
    mockServices(makeRun({ status: LoopRunStatus.Running }));
    const panel = await openActionTab(makeWorkItem());

    const live = within(panel).getByText("Live Output");
    expect(row(live).classList.contains("wiv2-bubble-row-ai")).toBe(true);
  });

  test("the feedback card closes the thread on the human side", async () => {
    mockServices(makeRun());
    const panel = await openActionTab(
      makeWorkItem({
        status: WorkItemStatus.HumanFeedback,
        humanFeedbackReason: "Human Input Needed",
        conversation: [
          { role: "ai", content: "done", timestamp: "2026-09-24T09:10:00Z", name: "Coder" },
        ],
      }),
    );

    const card = row(within(panel).getByText("Human Feedback"));
    expect(card.classList.contains("wiv2-bubble-row-human")).toBe(true);
    const rows = [...panel.querySelectorAll(".wiv2-bubble-row")];
    expect(rows[rows.length - 1]).toBe(card);
  });
});

describe("variables a turn set", () => {
  const run = makeRun({
    nodes: [
      execution("exec-1", "Coder", "2026-09-24T09:00:00Z", "2026-09-24T09:10:00Z"),
      execution("exec-2", "Coder", "2026-09-24T09:20:00Z", "2026-09-24T09:30:00Z"),
    ],
  });
  const writes: LoopRunVariableWrite[] = [
    {
      runId: "run-1",
      runNodeId: "exec-1",
      name: "summary",
      value: "draft",
      previousValue: null,
      writtenAt: "2026-09-24T09:05:00Z",
    },
    {
      runId: "run-1",
      runNodeId: "exec-1",
      name: "handoff",
      value: "for review",
      previousValue: null,
      writtenAt: "2026-09-24T09:06:00Z",
    },
    {
      runId: "run-1",
      runNodeId: "exec-2",
      name: "summary",
      value: "final",
      previousValue: "draft",
      writtenAt: "2026-09-24T09:25:00Z",
    },
  ];
  const conversation = [
    {
      role: "ai",
      content: "first turn",
      timestamp: "2026-09-24T09:10:01Z",
      name: "Coder",
      runNodeId: "exec-1",
    },
    {
      role: "ai",
      content: "second turn",
      timestamp: "2026-09-24T09:30:01Z",
      name: "Coder",
      runNodeId: "exec-2",
    },
  ];

  test("the thread reads the history in one request, not one per run", async () => {
    mockServices(run, writes);
    vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([
      run,
      makeRun({ id: "run-0" }),
      makeRun({ id: "run-older" }),
    ]);
    const getById = vi.spyOn(authServices.loopRunService, "getById").mockResolvedValue(run);
    const history = vi.spyOn(authServices.workItemService, "getVariableWrites");

    await openActionTab(makeWorkItem({ conversation }));

    expect(history).toHaveBeenCalledTimes(1);
    expect(new Set(getById.mock.calls.map(([id]) => id))).toEqual(new Set(["run-1"]));
  });

  test("each variable a turn touched gets its own pill, labelled new or changed", async () => {
    mockServices(run, writes);
    const panel = await openActionTab(makeWorkItem({ conversation }));

    const first = row(within(panel).getByText("first turn"));
    const second = row(within(panel).getByText("second turn"));
    const pills = (r: HTMLElement) =>
      [...r.querySelectorAll(".wiv2-turn-vars-toggle")].map((p) => p.textContent);
    expect(pills(first)).toEqual(["{x}handoffnew▸", "{x}summarynew▸"]);
    expect(pills(second)).toEqual(["{x}summarychanged▸"]);
  });

  test("a pill opens its own variable, showing the value that turn left", async () => {
    mockServices(run, writes);
    const panel = await openActionTab(makeWorkItem({ conversation }));
    const first = row(within(panel).getByText("first turn"));

    await act(async () => {
      fireEvent.click(within(first).getByRole("button", { name: /summary/ }));
    });

    expect(within(first).getByText("draft")).toBeTruthy();
    expect(within(first).getByText("changed again later")).toBeTruthy();
    expect(within(first).queryByText("for review")).toBeNull();
  });
});

describe("PR details", () => {
  const queued = [
    {
      id: "w1",
      kind: "reply" as const,
      targetId: "rc-1",
      body: "Dropped it to 150 ms.",
      path: "SearchBox.tsx",
      line: 42,
      queuedAt: "2026-09-24T10:05:00Z",
    },
    {
      id: "w2",
      kind: "resolve" as const,
      targetId: "rc-1",
      body: null,
      path: "SearchBox.tsx",
      line: 42,
      queuedAt: "2026-09-24T10:05:00Z",
    },
  ];

  test("is not shown for an item with no pull request", async () => {
    mockServices(makeRun());
    const panel = await openActionTab(makeWorkItem());

    expect(within(panel).queryByText("PR details")).toBeNull();
  });

  test("starts collapsed, counting what is waiting to be posted", async () => {
    mockServices(makeRun({ prSnapshot: snapshot(), prQueuedWrites: queued }));
    const panel = await openActionTab(makeWorkItem({ prUrl: "https://git.example/pr/1" }));

    const toggle = within(panel).getByRole("button", { name: /PR details/ });
    expect(toggle.getAttribute("aria-expanded")).toBe("false");
    expect(within(toggle).getByText("2 pending")).toBeTruthy();
    expect(within(panel).queryByText("250 ms feels sluggish.")).toBeNull();
    expect(within(panel).queryByText("Dropped it to 150 ms.")).toBeNull();
  });

  test("expanded, shows the pull request's thread and the replies waiting to go out", async () => {
    mockServices(makeRun({ prSnapshot: snapshot(), prQueuedWrites: queued }));
    const panel = await openActionTab(makeWorkItem({ prUrl: "https://git.example/pr/1" }));

    await act(async () => {
      fireEvent.click(within(panel).getByRole("button", { name: /PR details/ }));
    });

    expect(within(panel).getByText("250 ms feels sluggish.")).toBeTruthy();
    expect(within(panel).getByText("Dropped it to 150 ms.")).toBeTruthy();
  });

  test("a reply can be dropped from inside it", async () => {
    mockServices(makeRun({ prSnapshot: snapshot(), prQueuedWrites: queued }));
    const drop = vi.spyOn(authServices.loopRunService, "dropQueuedPrWrite").mockResolvedValue();
    const panel = await openActionTab(makeWorkItem({ prUrl: "https://git.example/pr/1" }));

    await act(async () => {
      fireEvent.click(within(panel).getByRole("button", { name: /PR details/ }));
    });
    await act(async () => {
      fireEvent.click(within(panel).getAllByRole("button", { name: "Drop" })[0]);
    });

    expect(drop).toHaveBeenCalledWith("run-1", "w1");
  });

  test("stays after everything has been posted, so a refused drop can still say so", async () => {
    mockServices(makeRun({ prQueuedWrites: [] }));
    const panel = await openActionTab(makeWorkItem({ prUrl: "https://git.example/pr/1" }));

    expect(within(panel).getByRole("button", { name: /PR details/ })).toBeTruthy();
  });

  test("links to the pull request once, from the panel rather than the feedback card", async () => {
    mockServices(makeRun({ prSnapshot: snapshot() }));
    const panel = await openActionTab(
      makeWorkItem({
        status: WorkItemStatus.HumanFeedback,
        humanFeedbackReason: "PR Awaiting Merge",
        prUrl: "https://git.example/pr/1",
      }),
    );

    const links = within(panel).getAllByRole("link", { name: "Open PR" });
    expect(links).toHaveLength(1);
    expect(links[0].closest(".wiv2-pr-details")).not.toBeNull();
  });
});
