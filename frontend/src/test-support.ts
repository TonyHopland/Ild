// Shared helpers for tests. Not a test file itself, so it is outside the
// `src/**/*.test.{ts,tsx}` include and never collected as a suite.
import { useSyncExternalStore } from "react";
import { act, fireEvent, screen, within } from "@testing-library/react";
import { vi, type Mock } from "vite-plus/test";
import type { SignalRConnection } from "./hooks/useSignalR";
import { workItemService } from "./services/auth";
import { compareServerOrder } from "./utils/taskboardColumns";
import { compareTags, matchesTaskboardFilter, sameTag } from "./utils/taskboardFilter";
import { WorkItemStatus } from "./types";
import type {
  ChatMessageAppendedPayload,
  ChatTurnCompletedPayload,
  ChatTurnProgressPayload,
  ChatTurnStartedPayload,
  GlobalVariableProposal,
  LoopTemplateProposal,
  WorkItem,
  WorkItemEditProposal,
} from "./types";
import type { TypedSignalRMessage } from "./types/signalr";

/** Presses before the loop gives up and reports the caller's own assertion. */
const MaxPresses = 40;

/** Yield between presses, long enough for React to run a passive effect. */
const YieldMs = 10;

/**
 * Presses Escape on the document until `settled` passes.
 *
 * Dialogs close from a document-level keydown listener that React attaches in a
 * passive effect, and passive effects run on React's own scheduler — nothing in
 * `render` guarantees that flush has happened by the time the next statement
 * runs. So a dialog can be in the DOM while its listener is not yet attached,
 * and a keydown has no queue: a press that lands in that window is swallowed
 * for good, leaving the test to wait out its timeout on a close that will never
 * come.
 *
 * Pressing inside the retry loop removes the ordering dependency — a swallowed
 * press just costs one more attempt. `settled` is checked immediately after
 * each press, so the loop stops on the first press that takes effect. That
 * matters where a second effective press would undo the first: a Discard
 * confirm has its own Escape-to-cancel listener.
 *
 * The budget is counted in PRESSES, not in wall-clock time, which is why this
 * does not use `waitFor`. Under `waitFor` the whole loop shares one 1s deadline,
 * and a suite this size does get stalled off the CPU for longer than that: one
 * stall is then enough to spend the entire budget, and the loop gives up having
 * pressed once or twice, however many presses the dialog was waiting for.
 * Measured on this machine — a single 2.5s stall leaves `waitFor` settling
 * after 2 presses and never reaching the 4 the swallow fixture needs, which is
 * the failure the gate hit. Counting presses has no deadline to spend, so a
 * stalled run is slow rather than red.
 */
export async function pressEscapeUntil(settled: () => void): Promise<void> {
  for (let press = 1; press < MaxPresses; press++) {
    fireEvent.keyDown(document, { key: "Escape" });
    try {
      settled();
      return;
    } catch {
      // Swallowed, or the state `settled` waits on has not rendered yet.
    }
    // Inside act so React's pending passive effects — including the one that
    // attaches the listener this press may have missed — run before the next.
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, YieldMs));
    });
  }

  // Out of presses: press once more and let `settled` throw, so a dialog that
  // never closes fails with the caller's own assertion rather than a timeout.
  fireEvent.keyDown(document, { key: "Escape" });
  settled();
}

/**
 * The chat hub events a bubble test simulates, each mapped to the payload the
 * server really sends (`SignalRPayloads.cs`). A test's `emit` helper is typed
 * against this, so an event built without its turn id is a compile error rather
 * than a handler call that quietly does nothing: every one of these carries a
 * turn id, and the bubble matches on it to tell a replaced turn's traffic from
 * its successor's — an untagged event is silently ignored, which is exactly how
 * a test can go on passing while exercising none of what it names.
 */
export interface ChatHubEvents {
  ChatTurnStarted: ChatTurnStartedPayload;
  ChatTurnProgress: ChatTurnProgressPayload;
  ChatMessageAppended: ChatMessageAppendedPayload;
  ChatTurnCompleted: ChatTurnCompletedPayload;
}

/**
 * The chat bubble's list of chats, opened from the panel header if it is closed.
 * In the default (narrow) panel it fills the panel, so the open chat or the start
 * form is not on screen while it is.
 */
export async function openChatList(): Promise<HTMLElement> {
  const shown = screen.queryByRole("complementary", { name: "Chats" });
  if (shown) return shown;
  fireEvent.click(await screen.findByRole("button", { name: "Chat list" }));
  return screen.findByRole("complementary", { name: "Chats" });
}

/** Opens a chat from the chat bubble's list, by the name its row shows. */
export async function openChatFromList(name: string): Promise<void> {
  fireEvent.click(await within(await openChatList()).findByText(name));
}

/** Shows the favourite, rename and delete actions of a chat in the chat bubble's list. */
export function showChatActions(name: string): void {
  const actions = screen.getByRole("button", { name: `Actions for chat ${name}` });
  if (actions.getAttribute("aria-expanded") !== "true") fireEvent.click(actions);
}

/** A board page request, as the Taskboard sends it for one column and the statuses it holds. */
export interface FakeBoardPageQuery {
  statuses: string[];
  search?: string;
  repositoryId?: string;
  tags?: string[];
  skip: number;
  take: number;
}

/** The board filter the per-status counts are taken under. */
export interface FakeBoardFilter {
  search?: string;
  repositoryId?: string;
  tags?: string[];
}

/** The board's own filter rules, with the server's tolerance of blank tags. */
function matchesFakeFilter(item: WorkItem, filter: FakeBoardFilter): boolean {
  return matchesTaskboardFilter(item, {
    search: filter.search ?? "",
    repositoryId: filter.repositoryId ?? "",
    tags: (filter.tags ?? []).filter((tag) => tag.trim() !== ""),
  });
}

/**
 * An in-memory stand-in for the board's listing endpoints (`/workitems/page`,
 * `/workitems/counts`, `/workitems/tags`) with the server's semantics: status,
 * repository, case-insensitive per-field search and every-tag filtering, newest
 * first with the id as tiebreaker, and totals that ignore skip/take. `items` is
 * the server's state; a test changes it to model changes made elsewhere.
 * `getAll` is stubbed too, because the detail dialog's dependency picker uses it.
 */
export function mockTaskboardServer(initial: WorkItem[]) {
  const items = [...initial];
  const page = (q: FakeBoardPageQuery) => {
    const matching = items
      .filter((wi) => q.statuses.includes(wi.status) && matchesFakeFilter(wi, q))
      .sort(compareServerOrder);
    return { items: matching.slice(q.skip, q.skip + q.take), total: matching.length };
  };
  const counts = (filter: FakeBoardFilter) => {
    const result: Record<string, number> = {};
    for (const status of Object.values(WorkItemStatus)) {
      result[status] = items.filter(
        (wi) => wi.status === status && matchesFakeFilter(wi, filter),
      ).length;
    }
    return result;
  };
  const tags = () =>
    items
      .flatMap((wi) => wi.tags ?? [])
      .sort(compareTags)
      .filter((tag, i, sorted) => i === 0 || !sameTag(sorted[i - 1], tag));
  const getPage = vi
    .spyOn(workItemService, "getPage")
    .mockImplementation(async (q: FakeBoardPageQuery) => page(q));
  const getCounts = vi
    .spyOn(workItemService, "getCounts")
    .mockImplementation(async (filter: FakeBoardFilter) => counts(filter));
  const getTags = vi.spyOn(workItemService, "getTags").mockImplementation(async () => tags());
  const getAll = vi.spyOn(workItemService, "getAll").mockResolvedValue([]);
  return { items, page, counts, tags, getPage, getCounts, getTags, getAll };
}

/** A request the stubbed `fetch` received. */
export interface Call {
  method: string;
  path: string;
  query: URLSearchParams;
  body: Record<string, unknown> | undefined;
}

/** A response with `body` as its JSON text, or no text without one. */
export function reply(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), { status });
}

/** Stubs `fetch` to answer each request with `answer`; returns every request, as it arrives. */
export function stubFetch(answer: (call: Call) => Response | Promise<Response>): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: string, init?: RequestInit) => {
      const url = new URL(input, "http://localhost");
      const call: Call = {
        method: (init?.method ?? "GET").toUpperCase(),
        path: url.pathname,
        query: url.searchParams,
        body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined,
      };
      calls.push(call);
      return answer(call);
    }),
  );
  return calls;
}

/** The labels, in the order their elements appear in `container`. */
export function documentOrder(labels: string[], container: HTMLElement = document.body): string[] {
  return labels
    .map((l) => [l, within(container).getByText(l, { exact: false })] as const)
    .sort(([, a], [, b]) =>
      a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1,
    )
    .map(([l]) => l);
}

/**
 * A handler a hub fake holds. Declared as a method, so the typed handler of any
 * one event is held without a cast; it is only ever called with that event.
 */
export interface HubListener {
  handle(msg: TypedSignalRMessage): void;
}

export type HubConnectionState = SignalRConnection["connectionState"];

/** A hub connection that lets a test push events to its listeners and change its state. */
export interface FakeHub {
  /** The listeners of each event, by the handler they were added with. */
  handlers: Record<string, Map<unknown, HubListener>>;
  invoke: Mock<(...args: unknown[]) => Promise<void>>;
  /** The same object for the hub's whole life; a hook re-rendered after `setState` sees the new state. */
  connection: SignalRConnection;
  setState: (state: HubConnectionState) => void;
  /**
   * Moves the connection to `state` inside act, re-rendering every component
   * holding it through {@link routeHubs} or {@link routeEveryHub}, as a drop or a
   * reconnect of the real hub does.
   */
  changeState: (state: HubConnectionState) => Promise<void>;
  /** Calls `listener` on every {@link FakeHub.changeState}; returns how to stop. */
  subscribe: (listener: () => void) => () => void;
  /** Calls every listener for `event`, outside act. */
  dispatch: (event: string, payload: unknown) => void;
  /** Calls every listener for `event`, inside an async act. */
  emit: (event: string, payload: unknown) => Promise<void>;
  /** Back to how the hub started: no listeners, its first state, and an invoke that resolves. */
  reset: () => void;
}

export function fakeHub(state: HubConnectionState = "connected"): FakeHub {
  const handlers: Record<string, Map<unknown, HubListener>> = {};
  const invoke = vi.fn((..._args: unknown[]) => Promise.resolve());
  const connection: SignalRConnection = {
    connectionState: state,
    on: <E extends string>(event: E, handler: (msg: TypedSignalRMessage<E>) => void) => {
      (handlers[event] ??= new Map()).set(handler, { handle: handler });
    },
    off: <E extends string>(event: E, handler: (msg: TypedSignalRMessage<E>) => void) => {
      handlers[event]?.delete(handler);
    },
    invoke,
  };
  const dispatch = (event: string, payload: unknown) => {
    for (const listener of Array.from(handlers[event]?.values() ?? []))
      listener.handle({ type: event, payload, timestamp: "" });
  };
  const emit = (event: string, payload: unknown) =>
    act(async () => {
      dispatch(event, payload);
    });
  const setState = (next: HubConnectionState) => {
    connection.connectionState = next;
  };
  const listeners = new Set<() => void>();
  const subscribe = (listener: () => void) => {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  };
  const changeState = (next: HubConnectionState) =>
    act(async () => {
      setState(next);
      for (const listener of Array.from(listeners)) listener();
    });
  const reset = () => {
    for (const event of Object.keys(handlers)) delete handlers[event];
    connection.connectionState = state;
    invoke.mockReset();
    invoke.mockImplementation((..._args: unknown[]) => Promise.resolve());
  };
  return { handlers, invoke, connection, setState, changeState, subscribe, dispatch, emit, reset };
}

/**
 * Answers `useSignalR` with the connection of `hubs[url]`, and of a hub that
 * stays disconnected for a url it does not name or for none; returns every url
 * asked for, in order. The test passes its own import of `hooks/useSignalR`:
 * importing it here would be circular for a file that mocks that module with
 * {@link fakeSignalRModule}.
 */
export function routeHubs(
  signalRModule: { useSignalR: (hubUrl: string | null) => SignalRConnection },
  hubs: Record<string, FakeHub>,
): (string | null)[] {
  const asked: (string | null)[] = [];
  const unrouted = fakeHub("disconnected");
  const useRoutedConnection = (hubUrl: string | null) => {
    asked.push(hubUrl);
    return useFakeConnection(hubUrl ? (hubs[hubUrl] ?? unrouted) : unrouted);
  };
  vi.spyOn(signalRModule, "useSignalR").mockImplementation(useRoutedConnection);
  return asked;
}

/** Answers `useSignalR` with the connection of `hub`, whatever hub is asked for. */
export function routeEveryHub(
  signalRModule: { useSignalR: (hubUrl: string | null) => SignalRConnection },
  hub: FakeHub,
): void {
  const useEveryConnection = () => useFakeConnection(hub);
  vi.spyOn(signalRModule, "useSignalR").mockImplementation(useEveryConnection);
}

/** The connection of `hub`, rendered again whenever its state changes. */
function useFakeConnection(hub: FakeHub): SignalRConnection {
  useSyncExternalStore(hub.subscribe, () => hub.connection.connectionState);
  return hub.connection;
}

/**
 * The hub every connection of {@link fakeSignalRModule} is. Each test file has
 * its own module instances, so this is one per file.
 */
export const signalRHub = fakeHub();

/**
 * A stand-in for `hooks/useSignalR`, for a test file's `vi.mock`, whose hook
 * returns {@link signalRHub}'s connection for every hub:
 * `vi.mock("../hooks/useSignalR", async () => (await import("../test-support")).fakeSignalRModule())`.
 */
export function fakeSignalRModule() {
  return { useSignalR: () => signalRHub.connection };
}

/** A pending loop template proposal made by run-1 of work item wi-1, adding a Lint step to tpl-1 at version 3. */
export function makeLoopTemplateProposal(
  overrides: Partial<LoopTemplateProposal> = {},
): LoopTemplateProposal {
  return {
    id: "lp-1",
    templateId: "tpl-1",
    templateName: "Release Loop",
    templateExists: true,
    baseVersion: 3,
    latestVersion: 3,
    rationale: "Builds keep failing on the slow runner.",
    status: "Pending",
    editedBeforeSave: false,
    appliedVersion: null,
    rejectionReason: null,
    createdAt: "2026-10-01T10:00:00Z",
    decidedAt: null,
    createdByLoopRunId: "run-1",
    createdByRunNodeId: "exec-1",
    createdByChatSessionId: null,
    chatReplySequence: null,
    source: { kind: "run", workItemId: "wi-1", chatSessionId: null, linkable: true },
    edits: null,
    changes: {
      nameChanged: false,
      descriptionChanged: false,
      recoveryPolicyChanged: false,
      nodesAdded: [{ id: "n-lint", label: "Lint", type: "Cmd" }],
      nodesRemoved: [],
      nodesChanged: [],
      edgesAdded: [],
      edgesRemoved: [],
    },
    ...overrides,
  };
}

/** A pending suggestion for the existing variable review_rules, made by run-1 of work item wi-1. */
export function makeGlobalVariableProposal(
  overrides: Partial<GlobalVariableProposal> = {},
): GlobalVariableProposal {
  return {
    id: "gp-1",
    name: "review_rules",
    value: "Keep reviews short.\nName the file and the line.",
    rationale: "Reviews keep missing where the problem is.",
    status: "Pending",
    rejectionReason: null,
    createdAt: "2026-10-01T10:00:00Z",
    decidedAt: null,
    snapshotUpdatedAt: "2026-09-30T10:00:00Z",
    currentValue: "Keep reviews short.\nName the file.",
    variableExists: true,
    createdByLoopRunId: "run-1",
    createdByRunNodeId: "exec-1",
    createdByChatSessionId: null,
    chatReplySequence: null,
    source: { kind: "run", workItemId: "wi-1", chatSessionId: null, linkable: true },
    ...overrides,
  };
}

/** A pending edit proposal p-1 retitling work item wi-1, made by neither a run nor a chat. */
export function makeEditProposal(
  overrides: Partial<WorkItemEditProposal> = {},
): WorkItemEditProposal {
  return {
    id: "p-1",
    workItemId: "wi-1",
    status: "Pending",
    proposed: { title: "New title" },
    snapshot: {
      title: "Old title",
      description: null,
      tags: [],
      branchNameOverride: null,
      baseBranchOverride: null,
    },
    rationale: null,
    rejectionReason: null,
    createdByLoopRunId: null,
    createdByChatSessionId: null,
    createdAt: "2026-09-26T10:00:00Z",
    decidedAt: null,
    ...overrides,
  };
}

/** The proposal card `element` is in; throws when it is in none. */
export function proposalCardAround(element: Element): HTMLElement {
  const card = element.closest<HTMLElement>('[data-testid="proposal-card"]');
  if (card === null) throw new Error("The element is not inside a proposal card.");
  return card;
}

/** Delivers a hub event to every listener of {@link signalRHub}, inside act. */
export function emitHubEvent(event: string, payload: unknown): void {
  act(() => {
    signalRHub.dispatch(event, payload);
  });
}

/**
 * Lets every answer already given reach the view, and every effect it causes run.
 * `rounds` is for a chain of reads, each started by an effect of the one before.
 */
export async function settle(rounds = 1): Promise<void> {
  for (let round = 0; round < rounds; round++) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

/**
 * Like {@link settle}, but lets no timer fire: for a test on fake timers, or one
 * whose next step must come before a timer the view has set.
 */
export async function flushPromises(): Promise<void> {
  for (let round = 0; round < 5; round++) {
    await act(async () => {
      for (let tick = 0; tick < 10; tick++) await Promise.resolve();
    });
  }
}

/** A promise a test settles by hand. */
export interface Deferred<T> {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason: unknown) => void;
}

export function deferred<T = void>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}
