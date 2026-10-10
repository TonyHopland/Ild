import { useSyncExternalStore } from "react";
import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, act, within, waitFor } from "@testing-library/react";
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
  TurnVariableChange,
  RemotePrSnapshot,
  WorkItemEditProposal,
} from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";
import { stageParkedNode, parkedRun } from "../../test-support.parkedNode";

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
    latestLoopRunId: "run-1",
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

type Conversation = Awaited<ReturnType<typeof authServices.loopRunService.getConversation>>;
type Message = Conversation["messages"][number];

function msg(
  id: number,
  runId: string,
  role: "ai" | "human" | "system",
  text: string,
  overrides: Partial<Message> = {},
): Message {
  return {
    id,
    runId,
    runNodeId: null,
    role,
    name: role === "ai" ? "Coder" : role === "human" ? "Human" : "LoopRunStarted",
    text,
    timestamp: `2026-09-24T09:${String(id).padStart(2, "0")}:00Z`,
    ...overrides,
  } as Message;
}

function conversation(
  runId: string,
  messages: Message[],
  lastEventId?: number | null,
): Conversation {
  return {
    runId,
    messages,
    lastEventId:
      lastEventId === undefined ? (messages[messages.length - 1]?.id ?? null) : lastEventId,
  } as Conversation;
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

async function flush() {
  await act(async () => {
    for (let i = 0; i < 10; i++) await Promise.resolve();
  });
}

type HubHandler = (m: { type: string; payload: unknown; timestamp: string }) => void;

/**
 * A run hub the test drives: `emit` delivers an event to every listener,
 * `setConnection` moves the connection state the way a drop and reconnect do.
 */
function mockHub() {
  const handlers = new Map<string, Set<HubHandler>>();
  const listeners = new Set<() => void>();
  let state = "connected";
  const on = vi.fn((event: string, h: HubHandler) => {
    const set = handlers.get(event) ?? new Set<HubHandler>();
    set.add(h);
    handlers.set(event, set);
  });
  const off = vi.fn((event: string, h: HubHandler) => handlers.get(event)?.delete(h));
  const invoke = vi.fn((..._args: unknown[]) => Promise.resolve(undefined as unknown));
  const subscribe = (l: () => void) => {
    listeners.add(l);
    return () => listeners.delete(l);
  };
  const useMockedSignalR = () => {
    const connectionState = useSyncExternalStore(subscribe, () => state);
    return { on, off, invoke, connectionState };
  };
  vi.spyOn(signalRHook, "useSignalR").mockImplementation(
    useMockedSignalR as unknown as typeof signalRHook.useSignalR,
  );
  const emit = (event: string, payload: unknown) =>
    act(async () => {
      handlers.get(event)?.forEach((h) => h({ type: event, payload, timestamp: "" }));
    });
  const setConnection = (next: string) =>
    act(async () => {
      state = next;
      listeners.forEach((l) => l());
    });
  const subscribedTo = () =>
    invoke.mock.calls.filter(([method]) => method === "SubscribeToRun").map(([, id]) => id);
  return { emit, setConnection, invoke, subscribedTo };
}

function eventLogged(runId: string, id: number, eventType = "NodeStarted") {
  return { runId, id, eventType, nodeId: null, runNodeId: null, timestamp: "2026-09-24T10:00:00Z" };
}

/** Every run read answers from `runs` by id; the conversation of each run is empty unless staged. */
function stageRuns(runs: LoopRun[]) {
  vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue(runs);
  const getById = vi
    .spyOn(authServices.loopRunService, "getById")
    .mockImplementation((id: string) => {
      const run = runs.find((r) => r.id === id);
      return run ? Promise.resolve(run) : Promise.reject({ status: 404, message: "not found" });
    });
  const getConversation = vi
    .spyOn(authServices.loopRunService, "getConversation")
    .mockImplementation((runId: string) => Promise.resolve(conversation(runId, [], null)));
  return { getById, getConversation };
}

const conversationReadsOf = (spy: { mock: { calls: unknown[][] } }, runId: string): unknown[][] =>
  spy.mock.calls.filter(([id]) => id === runId);

function mockServices(
  run: LoopRun,
  turnVariables: TurnVariableChange[] = [],
  messages: Message[] = [],
) {
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
  vi.spyOn(authServices.workItemService, "getTurnVariables").mockResolvedValue(turnVariables);
  vi.spyOn(authServices.loopRunService, "getEvents").mockResolvedValue({
    entries: [],
    nextCursor: 0,
  } as unknown as Awaited<ReturnType<typeof authServices.loopRunService.getEvents>>);
  vi.spyOn(authServices.loopRunService, "getConversation").mockImplementation((runId: string) =>
    Promise.resolve(conversation(runId, runId === run.id ? messages : [], null)),
  );
}

function dialog(workItem: WorkItem) {
  return (
    <MemoryRouter>
      <WorkItemModalV2 workItem={workItem} onClose={vi.fn()} onSave={vi.fn()} />
    </MemoryRouter>
  );
}

let rerenderDialog: (workItem: WorkItem) => Promise<void> = async () => {};

async function openActionTab(workItem: WorkItem) {
  await act(async () => {
    const view = render(dialog(workItem));
    rerenderDialog = async (next: WorkItem) => {
      await act(async () => {
        view.rerender(dialog(next));
      });
    };
    await Promise.resolve();
  });
  await act(async () => {
    fireEvent.click(screen.getByRole("tab", { name: /Action/ }));
    await Promise.resolve();
  });
  await flush();
  return document.getElementById("wiv2-panel-action") as HTMLElement;
}

/** The thread's entry holding `text`: the outermost element around it that holds no other entry's text. */
function entryOf(panel: HTMLElement, text: string, others: string[]): HTMLElement {
  let el = within(panel).getByText(text) as HTMLElement;
  while (
    el.parentElement &&
    el.parentElement !== panel &&
    !others.some((o) => el.parentElement!.textContent?.includes(o))
  )
    el = el.parentElement;
  return el;
}

const enabledButtons = (panel: HTMLElement, name: RegExp) =>
  within(panel)
    .queryAllByRole("button", { name })
    .filter((b) => !(b as HTMLButtonElement).disabled);

const row = (el: HTMLElement) => el.closest(".wiv2-bubble-row") as HTMLElement;

describe("the Action tab thread", () => {
  test("is the latest run's conversation in event order: AI turns on the AI side, replies on the human side, run events as compact rows, each with its time", async () => {
    const run = makeRun({ id: "run-B", status: LoopRunStatus.Completed });
    mockServices(
      run,
      [],
      [
        msg(3, "run-B", "system", "Run started from loop t", { name: "LoopRunStarted" }),
        msg(5, "run-B", "ai", "first turn"),
        msg(8, "run-B", "human", "my reply"),
        msg(12, "run-B", "ai", "second turn"),
      ],
    );
    const getConversation = vi.mocked(authServices.loopRunService.getConversation);
    const panel = await openActionTab(
      makeWorkItem({
        status: WorkItemStatus.Done,
        currentLoopRunId: null,
        latestLoopRunId: "run-B",
      }),
    );

    expect(getConversation).toHaveBeenCalled();
    expect(getConversation.mock.calls.every(([id]) => id === "run-B")).toBe(true);
    const texts = ["Run started from loop t", "first turn", "my reply", "second turn"];
    const entries = texts.map((t) =>
      entryOf(
        panel,
        t,
        texts.filter((o) => o !== t),
      ),
    );
    for (let i = 1; i < entries.length; i++)
      expect(
        entries[i - 1].compareDocumentPosition(entries[i]) & Node.DOCUMENT_POSITION_FOLLOWING,
      ).toBeTruthy();

    const [system, first, reply, second] = entries;
    expect(
      row(within(first).getByText("first turn")).classList.contains("wiv2-bubble-row-ai"),
    ).toBe(true);
    expect(
      row(within(reply).getByText("my reply")).classList.contains("wiv2-bubble-row-human"),
    ).toBe(true);
    expect(
      row(within(second).getByText("second turn")).classList.contains("wiv2-bubble-row-ai"),
    ).toBe(true);
    // An AI turn is labelled with its author; a human reply carries only its time.
    expect(within(first).getByText("Coder")).toBeTruthy();
    expect(within(reply).queryByText("You")).toBeNull();

    // A run event is neither an AI nor a human bubble, and names what happened.
    expect(
      within(system)
        .getByText("Run started from loop t")
        .closest(
          ".wiv2-bubble-row-ai, .wiv2-bubble-row-human, .wiv2-bubble-ai, .wiv2-bubble-human",
        ),
    ).toBeNull();
    expect(system.textContent).toContain("LoopRunStarted");

    const stamp = (id: number) =>
      new Date(`2026-09-24T09:${String(id).padStart(2, "0")}:00Z`).toLocaleString();
    expect(system.textContent).toContain(stamp(3));
    expect(first.textContent).toContain(stamp(5));
    expect(reply.textContent).toContain(stamp(8));
    expect(second.textContent).toContain(stamp(12));
  });

  test("the live run is shown in an AI bubble", async () => {
    mockServices(makeRun({ status: LoopRunStatus.Running }));
    const panel = await openActionTab(makeWorkItem());

    const live = within(panel).getByText("Live Output");
    expect(row(live).classList.contains("wiv2-bubble-row-ai")).toBe(true);
  });

  test("the feedback card closes the thread on the human side", async () => {
    stageParkedNode();
    mockServices(parkedRun("n-parked"), [], [msg(4, "run-1", "ai", "done")]);
    const panel = await openActionTab(
      makeWorkItem({
        status: WorkItemStatus.HumanFeedback,
        humanFeedbackReason: "Human Input Needed",
      }),
    );

    const card = row(await within(panel).findByText("Human Feedback"));
    expect(card.classList.contains("wiv2-bubble-row-human")).toBe(true);
    const rows = [...panel.querySelectorAll(".wiv2-bubble-row")];
    expect(rows[rows.length - 1]).toBe(card);
  });
});

describe("the run the thread shows", () => {
  test("a new run replaces the old run's messages at once, and nothing the old run sends late reaches it", async () => {
    const hub = mockHub();
    const runA = makeRun({ id: "run-A", status: LoopRunStatus.Running });
    const runB = makeRun({ id: "run-B", status: LoopRunStatus.Running });
    mockServices(runA);
    const { getById, getConversation } = stageRuns([runA, runB]);
    const runARead = deferred<LoopRun>();
    const laterOfA = deferred<Conversation>();
    const firstOfB = deferred<Conversation>();
    getById.mockImplementation((id: string) =>
      id === "run-A" ? runARead.promise : Promise.resolve(runB),
    );
    getConversation.mockImplementation((runId: string, after?: number) => {
      if (runId === "run-A")
        return after
          ? laterOfA.promise
          : Promise.resolve(conversation("run-A", [msg(2, "run-A", "ai", "A's turn")]));
      return firstOfB.promise;
    });
    const running = (runId: string) =>
      makeWorkItem({ currentLoopRunId: runId, latestLoopRunId: runId });

    const panel = await openActionTab(running("run-A"));
    await waitFor(() => expect(within(panel).getByText("A's turn")).toBeTruthy());

    // A's next event is being read when B starts.
    await hub.emit("EventLogged", eventLogged("run-A", 3));
    await flush();
    expect(getConversation).toHaveBeenCalledWith("run-A", 2);

    await rerenderDialog(running("run-B"));
    expect(within(panel).queryByText("A's turn")).toBeNull();

    // What A answers after the switch changes nothing: neither its messages nor
    // its run detail (halted, which would offer a Resume) are B's.
    await act(async () => {
      laterOfA.resolve(conversation("run-A", [msg(3, "run-A", "human", "A's late reply")]));
      runARead.resolve(
        makeRun({ id: "run-A", status: LoopRunStatus.WaitingHuman, isHalted: true }),
      );
    });
    await flush();
    expect(within(panel).queryByText("A's late reply")).toBeNull();
    expect(within(panel).queryByText("A's turn")).toBeNull();
    expect(enabledButtons(panel, /Resume/)).toEqual([]);

    const readsOfA = conversationReadsOf(getConversation, "run-A").length;
    await hub.emit("EventLogged", eventLogged("run-A", 9));
    await flush();
    expect(conversationReadsOf(getConversation, "run-A")).toHaveLength(readsOfA);

    await act(async () => {
      firstOfB.resolve(conversation("run-B", [msg(4, "run-B", "ai", "B's turn")]));
    });
    await waitFor(() => expect(within(panel).getByText("B's turn")).toBeTruthy());
    expect(within(panel).queryByText("A's turn")).toBeNull();
    expect(within(panel).queryByText("A's late reply")).toBeNull();
  });

  test("a finished run's messages are still there when the dialog is opened again", async () => {
    mockHub();
    const run = makeRun({
      id: "run-B",
      status: LoopRunStatus.Completed,
      completedAt: "2026-09-24T11:00:00Z",
    });
    mockServices(run);
    stageRuns([makeRun({ id: "run-A", status: LoopRunStatus.Failed }), run]);
    vi.mocked(authServices.loopRunService.getConversation).mockImplementation((runId: string) =>
      Promise.resolve(
        conversation(runId, runId === "run-B" ? [msg(6, "run-B", "ai", "All done.")] : []),
      ),
    );
    const done = makeWorkItem({
      status: WorkItemStatus.Done,
      currentLoopRunId: null,
      latestLoopRunId: "run-B",
    });

    let panel = await openActionTab(done);
    await waitFor(() => expect(within(panel).getByText("All done.")).toBeTruthy());

    cleanup();
    panel = await openActionTab({ ...done });
    await waitFor(() => expect(within(panel).getByText("All done.")).toBeTruthy());
  });

  test("an event after the last one read fetches only what follows it, and repeats never duplicate a message", async () => {
    const hub = mockHub();
    const run = makeRun({ id: "run-B", status: LoopRunStatus.Completed });
    mockServices(run);
    const { getConversation } = stageRuns([run]);
    // The newest event the run has logged so far; a read covers up to it.
    let logged = 6;
    getConversation.mockImplementation((runId: string, after?: number) => {
      if (!after)
        return Promise.resolve(
          conversation(runId, [msg(1, runId, "ai", "one"), msg(3, runId, "human", "three")], 4),
        );
      if (after === 4)
        return Promise.resolve(
          conversation(runId, [msg(3, runId, "human", "three"), msg(6, runId, "ai", "six")], 6),
        );
      return Promise.resolve(conversation(runId, [], Math.max(after, logged)));
    });
    const panel = await openActionTab(
      makeWorkItem({
        status: WorkItemStatus.Done,
        currentLoopRunId: null,
        latestLoopRunId: "run-B",
      }),
    );
    await waitFor(() => expect(within(panel).getByText("three")).toBeTruthy());

    await hub.emit("EventLogged", eventLogged("run-B", 5));
    await waitFor(() => expect(within(panel).getByText("six")).toBeTruthy());
    expect(getConversation).toHaveBeenCalledWith("run-B", 4);

    // The same event again, and one already covered by the read.
    await hub.emit("EventLogged", eventLogged("run-B", 5));
    await hub.emit("EventLogged", eventLogged("run-B", 6));
    await flush();

    const texts = ["one", "three", "six"];
    for (const t of texts) expect(within(panel).getAllByText(t)).toHaveLength(1);
    const entries = texts.map((t) => within(panel).getByText(t));
    expect(
      entries[0].compareDocumentPosition(entries[1]) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(
      entries[1].compareDocumentPosition(entries[2]) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();

    // An event that adds no message is read once, not over and over.
    const before = getConversation.mock.calls.length;
    logged = 7;
    await hub.emit("EventLogged", eventLogged("run-B", 7));
    await flush();
    await flush();
    const after = getConversation.mock.calls.length;
    expect(after - before).toBeGreaterThanOrEqual(1);
    expect(after - before).toBeLessThanOrEqual(2);
    await flush();
    expect(getConversation.mock.calls.length).toBe(after);
  });

  test.each([
    ["a finished item's completed run", WorkItemStatus.Done, LoopRunStatus.Completed],
    [
      "an item waiting on a person after its run failed",
      WorkItemStatus.HumanFeedback,
      LoopRunStatus.Failed,
    ],
  ])(
    "follows %s live, and reads it again when the connection comes back",
    async (_label, itemStatus, runStatus) => {
      const hub = mockHub();
      const run = makeRun({ id: "run-B", status: runStatus });
      mockServices(run);
      const { getConversation } = stageRuns([run]);
      await openActionTab(
        makeWorkItem({ status: itemStatus, currentLoopRunId: null, latestLoopRunId: "run-B" }),
      );

      expect(hub.subscribedTo()).toContain("run-B");
      const subscriptions = hub.subscribedTo().filter((id) => id === "run-B").length;
      const reads = conversationReadsOf(getConversation, "run-B").length;
      expect(reads).toBeGreaterThan(0);

      await hub.setConnection("reconnecting");
      await hub.setConnection("connected");
      await flush();

      expect(hub.subscribedTo().filter((id) => id === "run-B").length).toBeGreaterThan(
        subscriptions,
      );
      expect(conversationReadsOf(getConversation, "run-B").length).toBeGreaterThan(reads);
    },
  );

  test("cards a different run asked for close the thread, after the shown run's own", async () => {
    const run = makeRun({ id: "run-B", status: LoopRunStatus.Completed });
    mockServices(run, [], [msg(4, "run-B", "ai", "B's turn", { runNodeId: "exec-B1" })]);
    const card = (title: string, runId: string, runNodeId: string): WorkItemEditProposal => ({
      id: `p-${title}`,
      workItemId: "wi-1",
      status: "Pending",
      proposed: { title },
      snapshot: {
        title: "Old title",
        description: null,
        tags: [],
        branchNameOverride: null,
        baseBranchOverride: null,
      },
      rationale: null,
      rejectionReason: null,
      createdByLoopRunId: runId,
      createdByChatSessionId: null,
      createdByRunNodeId: runNodeId,
      chatReplySequence: null,
      createdAt: "2026-09-24T10:00:00Z",
      decidedAt: null,
    });
    vi.spyOn(authServices.workItemService, "listEditProposals").mockResolvedValue([]);
    vi.spyOn(authServices.workItemService, "listRequestedEditProposals").mockResolvedValue([
      card("From run A", "run-A", "exec-A1"),
      card("From run B", "run-B", "exec-B1"),
    ]);
    const panel = await openActionTab(
      makeWorkItem({
        status: WorkItemStatus.Done,
        currentLoopRunId: null,
        latestLoopRunId: "run-B",
        prUrl: "https://git.example/pr/1",
      }),
    );
    await waitFor(() => expect(within(panel).getByText("From run A")).toBeTruthy());
    await waitFor(() => expect(within(panel).getByText("B's turn")).toBeTruthy());

    const order = [
      within(panel).getByText("B's turn"),
      within(panel).getByText("From run B"),
      within(panel).getByRole("button", { name: /PR details/ }),
      within(panel).getByText("From run A"),
    ];
    for (let i = 1; i < order.length; i++)
      expect(
        order[i - 1].compareDocumentPosition(order[i]) & Node.DOCUMENT_POSITION_FOLLOWING,
      ).toBeTruthy();
  });
});

describe("the shown run's controls", () => {
  const parkedItem = (runId: string) =>
    makeWorkItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "Human Input Needed",
      currentLoopRunId: runId,
      latestLoopRunId: runId,
    });
  const feedbackIn = (panel: HTMLElement) => panel.querySelector(".wiv2-feedback") as HTMLElement;

  test("a reply goes to the run it was made for, and its draft, wait and error stay with that run", async () => {
    mockHub();
    stageParkedNode();
    mockServices(parkedRun("n-parked"));
    stageRuns([parkedRun("n-parked", { id: "run-A" }), parkedRun("n-parked", { id: "run-B" })]);
    const answerForA = deferred<void>();
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackInput")
      .mockImplementation((_id: string, runId: string) =>
        runId === "run-A"
          ? answerForA.promise
          : Promise.reject({
              status: 409,
              message: "Run run-B is no longer waiting for an answer.",
            }),
      );

    const panel = await openActionTab(parkedItem("run-A"));
    await within(panel).findByRole("button", { name: "Approve" });
    await act(async () => {
      fireEvent.change(feedbackIn(panel).querySelector("textarea") as HTMLTextAreaElement, {
        target: { value: "for A" },
      });
    });
    await act(async () => {
      fireEvent.click(within(panel).getByRole("button", { name: "Approve" }));
    });
    expect(answer).toHaveBeenCalledWith("wi-1", "run-A", "for A");

    await rerenderDialog(parkedItem("run-B"));
    await waitFor(() => expect(enabledButtons(panel, /^Approve$/)).toHaveLength(1));
    expect((feedbackIn(panel).querySelector("textarea") as HTMLTextAreaElement).value).toBe("");

    await act(async () => {
      answerForA.reject({ status: 409, message: "A was answered elsewhere." });
    });
    await flush();
    expect(panel.textContent).not.toContain("A was answered elsewhere.");
    expect(enabledButtons(panel, /^Approve$/)).toHaveLength(1);

    await act(async () => {
      fireEvent.click(within(panel).getByRole("button", { name: "Approve" }));
    });
    expect(answer).toHaveBeenLastCalledWith("wi-1", "run-B", "");
    await waitFor(() =>
      expect(feedbackIn(panel).textContent).toContain(
        "Run run-B is no longer waiting for an answer.",
      ),
    );

    await rerenderDialog(parkedItem("run-A"));
    await flush();
    expect(panel.textContent).not.toContain("Run run-B is no longer waiting for an answer.");
  });

  test.each([LoopRunStatus.Completed, LoopRunStatus.Failed, LoopRunStatus.Cancelled])(
    "a %s run offers no reply, halt or resume, whatever the item still says",
    async (status) => {
      mockHub();
      stageParkedNode();
      const run = parkedRun("n-parked", { id: "run-B", status, isHalted: true });
      mockServices(run);
      const { getById } = stageRuns([run]);
      const panel = await openActionTab(parkedItem("run-B"));
      await waitFor(() => expect(getById).toHaveBeenCalledWith("run-B"));
      await flush();

      expect(enabledButtons(panel, /Approve|Reject|Halt|Resume/)).toEqual([]);
    },
  );

  test("halt is offered while the shown run's AI node runs, and halts that run", async () => {
    mockHub();
    const run = makeRun({
      id: "run-B",
      status: LoopRunStatus.Running,
      nodes: [
        {
          ...execution("exec-1", "Coder", "2026-09-24T09:00:00Z", "2026-09-24T09:10:00Z"),
          status: LoopRunNodeStatus.Running,
          completedAt: null,
          nodeType: "AI",
        },
      ],
    });
    mockServices(run);
    stageRuns([makeRun({ id: "run-A", status: LoopRunStatus.Completed }), run]);
    const halt = vi.spyOn(authServices.loopRunService, "halt").mockResolvedValue();
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(
      makeWorkItem({ currentLoopRunId: "run-B", latestLoopRunId: "run-B" }),
    );
    const panel = await openActionTab(
      makeWorkItem({ currentLoopRunId: "run-B", latestLoopRunId: "run-B" }),
    );

    const button = await within(panel).findByRole("button", { name: /Halt AI node/ });
    await act(async () => {
      fireEvent.click(button);
    });

    expect(halt).toHaveBeenCalledWith("run-B");
  });
});

describe("why an item waits on a person when no run says so", () => {
  const reason = "Failed to start run: Matched template t has a broken graph";
  const waiting = (overrides: Partial<WorkItem>) =>
    makeWorkItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: null,
      statusReason: reason,
      statusReasonAt: "2026-09-24T10:00:00Z",
      ...overrides,
    });

  test.each([
    ["the item has no run", true, null, null],
    ["the reason is newer than the latest run", true, "run-B", "2026-09-24T09:00:00Z"],
    ["the latest run is newer than the reason", false, "run-B", "2026-09-24T11:00:00Z"],
  ])("when %s, the reason is shown: %s", async (_label, shown, runId, startedAt) => {
    mockHub();
    const run = makeRun({ id: "run-B", status: LoopRunStatus.Failed, startedAt: startedAt ?? "" });
    mockServices(run);
    stageRuns(runId ? [run] : []);
    const panel = await openActionTab(waiting({ currentLoopRunId: null, latestLoopRunId: runId }));
    await flush();

    if (shown) expect(await within(panel).findByText(reason)).toBeTruthy();
    else expect(within(panel).queryByText(reason)).toBeNull();
    expect(enabledButtons(panel, /Approve|Reject/)).toEqual([]);
  });

  test("is not shown until the latest run has been read", async () => {
    mockHub();
    const run = makeRun({
      id: "run-B",
      status: LoopRunStatus.Failed,
      startedAt: "2026-09-24T09:00:00Z",
    });
    mockServices(run);
    const { getById } = stageRuns([run]);
    const read = deferred<LoopRun>();
    getById.mockImplementation(() => read.promise);
    const panel = await openActionTab(
      waiting({ currentLoopRunId: null, latestLoopRunId: "run-B" }),
    );

    expect(within(panel).queryByText(reason)).toBeNull();

    await act(async () => {
      read.resolve(run);
    });
    expect(await within(panel).findByText(reason)).toBeTruthy();
  });
});

describe("variables a turn set", () => {
  const run = makeRun({
    nodes: [
      execution("exec-1", "Coder", "2026-09-24T09:00:00Z", "2026-09-24T09:10:00Z"),
      execution("exec-2", "Coder", "2026-09-24T09:20:00Z", "2026-09-24T09:30:00Z"),
    ],
  });
  const writes: TurnVariableChange[] = [
    {
      runId: "run-1",
      runNodeId: "exec-1",
      name: "summary",
      value: "draft",
      change: "created",
      changedLater: true,
    },
    {
      runId: "run-1",
      runNodeId: "exec-1",
      name: "handoff",
      value: "for review",
      change: "created",
      changedLater: false,
    },
    {
      runId: "run-1",
      runNodeId: "exec-2",
      name: "summary",
      value: "final",
      change: "changed",
      changedLater: false,
    },
  ];
  const turns = [
    msg(11, "run-1", "ai", "first turn", { runNodeId: "exec-1" }),
    msg(31, "run-1", "ai", "second turn", { runNodeId: "exec-2" }),
  ];

  test("the thread reads the history in one request for the item, not one per run", async () => {
    mockServices(run, writes, turns);
    vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([
      run,
      makeRun({ id: "run-0" }),
      makeRun({ id: "run-older" }),
    ]);
    const getById = vi.spyOn(authServices.loopRunService, "getById").mockResolvedValue(run);
    const history = vi.spyOn(authServices.workItemService, "getTurnVariables");

    const panel = await openActionTab(makeWorkItem());
    await waitFor(() => expect(within(panel).getByText("second turn")).toBeTruthy());

    expect(history).toHaveBeenCalled();
    expect(history.mock.calls.every(([id]) => id === "wi-1")).toBe(true);
    expect(new Set(getById.mock.calls.map(([id]) => id))).toEqual(new Set(["run-1"]));
  });

  test("each variable a turn touched gets its own pill, labelled new or changed", async () => {
    mockServices(run, writes, turns);
    const panel = await openActionTab(makeWorkItem());

    const first = row(await within(panel).findByText("first turn"));
    const second = row(within(panel).getByText("second turn"));
    const pills = (r: HTMLElement) =>
      [...r.querySelectorAll(".wiv2-turn-vars-toggle")].map((p) => p.textContent);
    await waitFor(() => expect(pills(first)).toEqual(["{x}handoffnew▸", "{x}summarynew▸"]));
    expect(pills(second)).toEqual(["{x}summarychanged▸"]);
  });

  test("a pill opens its own variable, showing the value that turn left", async () => {
    mockServices(run, writes, turns);
    const panel = await openActionTab(makeWorkItem());
    const first = row(await within(panel).findByText("first turn"));
    await waitFor(() =>
      expect(within(first).getByRole("button", { name: /summary/ })).toBeTruthy(),
    );

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

  test("counts as content, so the tab does not also say nothing is there", async () => {
    // Nothing else is in the thread: no conversation, no live run, no feedback.
    mockServices(makeRun({ prSnapshot: snapshot() }));
    const panel = await openActionTab(
      makeWorkItem({ status: WorkItemStatus.Done, prUrl: "https://git.example/pr/1" }),
    );

    expect(within(panel).getByRole("button", { name: /PR details/ })).toBeTruthy();
    expect(within(panel).queryByText("No action required.")).toBeNull();
  });

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
