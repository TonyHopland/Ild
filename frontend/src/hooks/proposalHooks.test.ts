import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act, waitFor } from "@testing-library/react";
import { useGlobalVariableProposals } from "./useGlobalVariableProposals";
import { useLoopTemplateProposals } from "./useLoopTemplateProposals";
import { usePendingGlobalVariableProposalCount } from "./usePendingGlobalVariableProposalCount";
import { usePendingLoopTemplateProposalCounts } from "./usePendingLoopTemplateProposalCounts";
import * as signalRHook from "./useSignalR";
import { globalVariableService, loopTemplateProposalService } from "../services/auth";
import type { LoopTemplateProposal } from "../types";

// The list and pending-count hooks of the two local proposal kinds (global
// variable suggestions, loop template proposals) keep one set of rules: join
// the hub group before every snapshot read, on connect and reconnect; re-read
// on a hint for the target; never show a late answer for an older read or an
// earlier target; for counts, the latest read wins and a failed one shows none.

type ConnectionState = "disconnected" | "connecting" | "connected" | "reconnecting";
type Handler = (msg: { payload: unknown }) => void;

interface FakeHub {
  state: ConnectionState;
  handlers: Map<string, Set<Handler>>;
  invoke: ReturnType<typeof vi.fn>;
  api: signalRHook.SignalRConnection;
}

function fakeHub(state: ConnectionState): FakeHub {
  const hub = {
    state,
    handlers: new Map<string, Set<Handler>>(),
    invoke: vi.fn((..._args: unknown[]) => Promise.resolve()),
  } as FakeHub;
  const on = (event: string, handler: Handler) => {
    const set = hub.handlers.get(event) ?? new Set<Handler>();
    set.add(handler);
    hub.handlers.set(event, set);
  };
  const off = (event: string, handler: Handler) => {
    hub.handlers.get(event)?.delete(handler);
  };
  Object.defineProperty(hub, "api", {
    get: () =>
      ({
        connectionState: hub.state,
        on,
        off,
        invoke: hub.invoke,
      }) as unknown as signalRHook.SignalRConnection,
  });
  return hub;
}

/** The connections the hook opens itself, by hub URL; returns the URLs it asked for. */
function useHubs(hubs: Record<string, FakeHub>) {
  const asked: (string | null | undefined)[] = [];
  vi.spyOn(signalRHook, "useSignalR").mockImplementation((hubUrl?: string | null) => {
    asked.push(hubUrl);
    return (hubUrl ? hubs[hubUrl] : fakeHub("disconnected")).api;
  });
  return asked;
}

function emit(hub: FakeHub, event: string, payload: unknown) {
  act(() => {
    hub.handlers.get(event)?.forEach((h) => h({ payload }));
  });
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (err: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

/** Reads the test answers one by one, in any order. */
function heldReads<T>() {
  const reads: ReturnType<typeof deferred<T>>[] = [];
  const read = () => {
    const next = deferred<T>();
    reads.push(next);
    return next.promise;
  };
  return { reads, read };
}

type Proposal = { id: string };
type Target = { workItemId: string } | { chatSessionId: string };

interface ListKind {
  name: string;
  use: (target: Target) => { proposals: Proposal[] | undefined; refresh: () => void };
  spyList: () => ReturnType<typeof vi.fn>;
  itemHint: string;
  chatHint: string;
  /** What a work item's hint carries. */
  itemPayload: unknown;
}

const listKinds: ListKind[] = [
  {
    name: "global variable suggestions",
    use: (target) => useGlobalVariableProposals(target),
    spyList: () =>
      vi.spyOn(globalVariableService, "listProposals") as unknown as ReturnType<typeof vi.fn>,
    itemHint: "GlobalVariableProposalsChanged",
    chatHint: "ChatGlobalVariableProposalsChanged",
    itemPayload: {},
  },
  {
    name: "loop template proposals",
    use: (target) => useLoopTemplateProposals(target),
    spyList: () =>
      vi.spyOn(loopTemplateProposalService, "list") as unknown as ReturnType<typeof vi.fn>,
    itemHint: "LoopTemplateProposalsChanged",
    chatHint: "ChatLoopTemplateProposalsChanged",
    itemPayload: { templateId: "tpl-any" },
  },
];

const lt = (id: string) => ({ id }) as LoopTemplateProposal;
const ids = (list: Proposal[] | undefined) => list?.map((p) => p.id);

afterEach(() => {
  vi.restoreAllMocks();
});

describe.each(listKinds)("the list of $name", (kind) => {
  test("for a work item: joins the board before every read, on connect and on reconnect, and a hint re-reads", async () => {
    const board = fakeHub("connecting");
    useHubs({ "/hubs/work-item": board, "/hubs/chat": fakeHub("connected") });
    const join = deferred<void>();
    board.invoke.mockImplementation(() => join.promise);
    let onServer: Proposal[] = [{ id: "p-1" }];
    const list = kind.spyList().mockImplementation(async () => onServer);

    const { result, rerender } = renderHook(() => kind.use({ workItemId: "wi-1" }));
    expect(list).not.toHaveBeenCalled();

    board.state = "connected";
    rerender();
    await waitFor(() => expect(board.invoke).toHaveBeenCalledWith("SubscribeToWorkItems"));
    expect(list).not.toHaveBeenCalled();
    await act(async () => join.resolve());
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1"]));
    expect(list).toHaveBeenCalledWith({ workItemId: "wi-1" });

    onServer = [{ id: "p-1" }, { id: "p-2" }];
    emit(board, kind.itemHint, kind.itemPayload);
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1", "p-2"]));

    const rejoin = deferred<void>();
    board.invoke.mockImplementation(() => rejoin.promise);
    board.invoke.mockClear();
    const readsBefore = list.mock.calls.length;
    board.state = "reconnecting";
    rerender();
    onServer = [{ id: "p-3" }];
    board.state = "connected";
    rerender();
    await waitFor(() => expect(board.invoke).toHaveBeenCalledWith("SubscribeToWorkItems"));
    expect(list).toHaveBeenCalledTimes(readsBefore);
    await act(async () => rejoin.resolve());
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-3"]));
  });

  test("a join that fails reads nothing", async () => {
    const board = fakeHub("connected");
    useHubs({ "/hubs/work-item": board, "/hubs/chat": fakeHub("connected") });
    vi.spyOn(console, "error").mockImplementation(() => {});
    board.invoke.mockImplementation(() => Promise.reject(new Error("join refused")));
    const list = kind.spyList().mockResolvedValue([]);

    const { result } = renderHook(() => kind.use({ workItemId: "wi-1" }));

    await waitFor(() => expect(board.invoke).toHaveBeenCalledWith("SubscribeToWorkItems"));
    await act(async () => {});
    expect(list).not.toHaveBeenCalled();
    expect(result.current.proposals).toBeUndefined();
  });

  test("for a chat: joins its group by id before the read, re-reads on its own hints only, and leaves on unmount", async () => {
    const chatHub = fakeHub("connected");
    useHubs({ "/hubs/work-item": fakeHub("connected"), "/hubs/chat": chatHub });
    const join = deferred<void>();
    chatHub.invoke.mockImplementation((method: unknown) =>
      method === "SubscribeToChat" ? join.promise : Promise.resolve(),
    );
    let onServer: Proposal[] = [{ id: "p-1" }];
    const list = kind.spyList().mockImplementation(async () => onServer);

    const { result, unmount } = renderHook(() => kind.use({ chatSessionId: "chat-1" }));
    await waitFor(() => expect(chatHub.invoke).toHaveBeenCalledWith("SubscribeToChat", "chat-1"));
    expect(list).not.toHaveBeenCalled();
    await act(async () => join.resolve());
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1"]));
    expect(list).toHaveBeenCalledWith({ chatSessionId: "chat-1" });

    const readsBefore = list.mock.calls.length;
    onServer = [{ id: "p-1" }, { id: "p-2" }];
    emit(chatHub, kind.chatHint, { chatSessionId: "chat-other" });
    expect(list).toHaveBeenCalledTimes(readsBefore);
    emit(chatHub, kind.chatHint, { chatSessionId: "chat-1" });
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1", "p-2"]));

    unmount();
    expect(chatHub.invoke).toHaveBeenCalledWith("UnsubscribeFromChat", "chat-1");
  });

  test("a late answer to an older read never replaces a newer one", async () => {
    const board = fakeHub("connected");
    useHubs({ "/hubs/work-item": board, "/hubs/chat": fakeHub("connected") });
    const { reads, read } = heldReads<Proposal[]>();
    kind.spyList().mockImplementation(read);

    const { result } = renderHook(() => kind.use({ workItemId: "wi-1" }));
    await waitFor(() => expect(reads.length).toBe(1));
    emit(board, kind.itemHint, kind.itemPayload);
    await waitFor(() => expect(reads.length).toBe(2));

    await act(async () => reads[1].resolve([{ id: "p-new" }]));
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-new"]));
    await act(async () => reads[0].resolve([{ id: "p-old" }]));
    expect(ids(result.current.proposals)).toEqual(["p-new"]);
  });

  test("a late answer for the previous target is never shown for the next", async () => {
    useHubs({ "/hubs/work-item": fakeHub("connected"), "/hubs/chat": fakeHub("connected") });
    const forFirst = deferred<Proposal[]>();
    kind
      .spyList()
      .mockImplementation(async (filter: { workItemId?: string }) =>
        filter.workItemId === "wi-1" ? forFirst.promise : [{ id: "p-2" }],
      );

    const { result, rerender } = renderHook(
      ({ workItemId }: { workItemId: string }) => kind.use({ workItemId }),
      { initialProps: { workItemId: "wi-1" } },
    );
    rerender({ workItemId: "wi-2" });
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-2"]));

    await act(async () => forFirst.resolve([{ id: "p-1" }]));
    expect(ids(result.current.proposals)).toEqual(["p-2"]);
  });
});

describe("the pending loop template proposals of one template", () => {
  test("read on the hub the caller holds, opening none of its own, and re-read on that template's hints only", async () => {
    const asked = useHubs({ "/hubs/work-item": fakeHub("connected") });
    const shared = fakeHub("connected");
    let onServer = [lt("p-1")];
    const list = vi
      .spyOn(loopTemplateProposalService, "list")
      .mockImplementation(async () => onServer);

    const { result } = renderHook(() =>
      useLoopTemplateProposals({ templateId: "tpl-1" }, shared.api),
    );
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1"]));
    expect(list).toHaveBeenCalledWith({ templateId: "tpl-1", status: "Pending" });
    expect(shared.invoke).toHaveBeenCalledWith("SubscribeToWorkItems");
    expect(asked.every((url) => url === null)).toBe(true);

    const readsBefore = list.mock.calls.length;
    onServer = [lt("p-1"), lt("p-2")];
    emit(shared, "LoopTemplateProposalsChanged", { templateId: "tpl-other" });
    expect(list).toHaveBeenCalledTimes(readsBefore);
    emit(shared, "LoopTemplateProposalsChanged", { templateId: "tpl-1" });
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1", "p-2"]));
  });
});

interface CountKind<T> {
  name: string;
  use: () => T;
  spyRead: () => ReturnType<typeof vi.fn>;
  hint: string;
  hintPayload: unknown;
  some: T;
  more: T;
  none: T;
}

const countKinds = [
  {
    name: "global variable suggestions",
    use: () => usePendingGlobalVariableProposalCount(),
    spyRead: () =>
      vi.spyOn(globalVariableService, "pendingProposalCount") as unknown as ReturnType<
        typeof vi.fn
      >,
    hint: "GlobalVariableProposalsChanged",
    hintPayload: {},
    some: 2,
    more: 5,
    none: 0,
  } satisfies CountKind<number>,
  {
    name: "loop template proposals",
    use: () => usePendingLoopTemplateProposalCounts(),
    spyRead: () =>
      vi.spyOn(loopTemplateProposalService, "pendingCounts") as unknown as ReturnType<typeof vi.fn>,
    hint: "LoopTemplateProposalsChanged",
    hintPayload: { templateId: "tpl-1" },
    some: { "tpl-1": 2 },
    more: { "tpl-1": 5, "tpl-2": 1 },
    none: {},
  } satisfies CountKind<Record<string, number>>,
] as CountKind<unknown>[];

describe.each(countKinds)("the pending count of $name", (kind) => {
  test("joins the board before every read, on connect and on reconnect, and a hint re-reads", async () => {
    const board = fakeHub("connected");
    useHubs({ "/hubs/work-item": board });
    const join = deferred<void>();
    board.invoke.mockImplementation(() => join.promise);
    let onServer = kind.some;
    const read = kind.spyRead().mockImplementation(async () => onServer);

    const { result, rerender } = renderHook(() => kind.use());
    expect(result.current).toEqual(kind.none);
    await waitFor(() => expect(board.invoke).toHaveBeenCalledWith("SubscribeToWorkItems"));
    expect(read).not.toHaveBeenCalled();
    await act(async () => join.resolve());
    await waitFor(() => expect(result.current).toEqual(kind.some));

    onServer = kind.more;
    emit(board, kind.hint, kind.hintPayload);
    await waitFor(() => expect(result.current).toEqual(kind.more));

    const rejoin = deferred<void>();
    board.invoke.mockImplementation(() => rejoin.promise);
    board.invoke.mockClear();
    const readsBefore = read.mock.calls.length;
    board.state = "reconnecting";
    rerender();
    onServer = kind.some;
    board.state = "connected";
    rerender();
    await waitFor(() => expect(board.invoke).toHaveBeenCalledWith("SubscribeToWorkItems"));
    expect(read).toHaveBeenCalledTimes(readsBefore);
    await act(async () => rejoin.resolve());
    await waitFor(() => expect(result.current).toEqual(kind.some));
  });

  test("only the latest read counts, and a failed one shows none rather than an old count", async () => {
    const board = fakeHub("connected");
    useHubs({ "/hubs/work-item": board });
    vi.spyOn(console, "error").mockImplementation(() => {});
    const { reads, read } = heldReads<unknown>();
    kind.spyRead().mockImplementation(read);

    const { result } = renderHook(() => kind.use());
    await waitFor(() => expect(reads.length).toBe(1));
    await act(async () => reads[0].resolve(kind.some));
    await waitFor(() => expect(result.current).toEqual(kind.some));

    emit(board, kind.hint, kind.hintPayload);
    emit(board, kind.hint, kind.hintPayload);
    await waitFor(() => expect(reads.length).toBe(3));
    await act(async () => reads[2].resolve(kind.more));
    await waitFor(() => expect(result.current).toEqual(kind.more));
    await act(async () => reads[1].resolve(kind.some));
    expect(result.current).toEqual(kind.more);

    emit(board, kind.hint, kind.hintPayload);
    await waitFor(() => expect(reads.length).toBe(4));
    await act(async () => reads[3].reject(new Error("read failed")));
    await waitFor(() => expect(result.current).toEqual(kind.none));
  });
});

describe("the pending loop template proposal counts on a hub the caller holds", () => {
  test("open no connection of their own and join and listen on the given one", async () => {
    const asked = useHubs({ "/hubs/work-item": fakeHub("connected") });
    const shared = fakeHub("connected");
    let onServer: Record<string, number> = { "tpl-1": 1 };
    vi.spyOn(loopTemplateProposalService, "pendingCounts").mockImplementation(async () => onServer);

    const { result } = renderHook(() => usePendingLoopTemplateProposalCounts(shared.api));
    await waitFor(() => expect(result.current).toEqual({ "tpl-1": 1 }));
    expect(shared.invoke).toHaveBeenCalledWith("SubscribeToWorkItems");
    expect(asked.every((url) => url === null)).toBe(true);

    onServer = { "tpl-1": 3 };
    emit(shared, "LoopTemplateProposalsChanged", { templateId: "tpl-1" });
    await waitFor(() => expect(result.current).toEqual({ "tpl-1": 3 }));
  });
});
