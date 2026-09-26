import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act, waitFor } from "@testing-library/react";
import { useEditProposals } from "./useEditProposals";
import * as signalRHook from "./useSignalR";
import * as authServices from "../services/auth";
import type { WorkItemEditProposal } from "../types";

type ConnectionState = "disconnected" | "connecting" | "connected" | "reconnecting";
type Handler = (msg: { payload: unknown }) => void;

/** One fake hub connection per hub URL, as the real hook opens one per URL. */
interface FakeHub {
  state: ConnectionState;
  handlers: Map<string, Set<Handler>>;
  invoke: ReturnType<typeof vi.fn>;
  api: ReturnType<typeof signalRHook.useSignalR>;
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
      }) as unknown as ReturnType<typeof signalRHook.useSignalR>,
  });
  return hub;
}

function useHubs(hubs: Record<string, FakeHub>) {
  vi.spyOn(signalRHook, "useSignalR").mockImplementation(
    (hubUrl?: string) => hubs[hubUrl ?? "/hubs/work-item"].api,
  );
}

function emit(hub: FakeHub, event: string, payload: unknown) {
  act(() => {
    hub.handlers.get(event)?.forEach((h) => h({ payload }));
  });
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

function proposal(id: string, overrides: Partial<WorkItemEditProposal> = {}): WorkItemEditProposal {
  return {
    id,
    workItemId: "wi-1",
    status: "Pending",
    proposed: { title: `Title from ${id}` },
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

const ids = (list: WorkItemEditProposal[] | undefined) => (list ?? []).map((p) => p.id);

afterEach(() => {
  vi.restoreAllMocks();
});

describe("useEditProposals for a work item", () => {
  test("reads the item's proposals and re-reads on a hint for that item only", async () => {
    const hub = fakeHub("connected");
    useHubs({ "/hubs/work-item": hub, "/hubs/chat": fakeHub("connected") });
    let onServer = [proposal("p-1")];
    const list = vi
      .spyOn(authServices.workItemService, "listEditProposals")
      .mockImplementation(async () => onServer);

    const { result } = renderHook(() => useEditProposals({ workItemId: "wi-1" }));

    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1"]));
    expect(list).toHaveBeenCalledWith("wi-1");
    const readsAfterMount = list.mock.calls.length;

    onServer = [proposal("p-1", { status: "Approved" })];
    emit(hub, "WorkItemEditProposalsChanged", { workItemId: "wi-other" });
    expect(list).toHaveBeenCalledTimes(readsAfterMount);

    emit(hub, "WorkItemEditProposalsChanged", { workItemId: "wi-1" });
    await waitFor(() => expect(result.current.proposals?.[0]?.status).toBe("Approved"));
  });

  test("a late answer to an older read is never applied over a newer one", async () => {
    const hub = fakeHub("connected");
    useHubs({ "/hubs/work-item": hub, "/hubs/chat": fakeHub("connected") });
    const reads: ReturnType<typeof deferred<WorkItemEditProposal[]>>[] = [];
    vi.spyOn(authServices.workItemService, "listEditProposals").mockImplementation(() => {
      const read = deferred<WorkItemEditProposal[]>();
      reads.push(read);
      return read.promise;
    });

    const { result } = renderHook(() => useEditProposals({ workItemId: "wi-1" }));
    await waitFor(() => expect(reads.length).toBeGreaterThan(0));
    const olderReads = reads.length;
    emit(hub, "WorkItemEditProposalsChanged", { workItemId: "wi-1" });
    await waitFor(() => expect(reads.length).toBeGreaterThan(olderReads));

    await act(async () => {
      reads[reads.length - 1].resolve([proposal("p-new", { status: "Stale" })]);
    });
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-new"]));

    await act(async () => {
      for (const read of reads.slice(0, olderReads)) read.resolve([proposal("p-old")]);
    });
    expect(ids(result.current.proposals)).toEqual(["p-new"]);
  });

  test("a late answer for the previous item is never applied to the next", async () => {
    useHubs({ "/hubs/work-item": fakeHub("connected"), "/hubs/chat": fakeHub("connected") });
    const forFirstItem = deferred<WorkItemEditProposal[]>();
    vi.spyOn(authServices.workItemService, "listEditProposals").mockImplementation((id: string) =>
      id === "wi-1"
        ? forFirstItem.promise
        : Promise.resolve([proposal("p-2", { workItemId: "wi-2" })]),
    );

    const { result, rerender } = renderHook(
      ({ workItemId }: { workItemId: string }) => useEditProposals({ workItemId }),
      { initialProps: { workItemId: "wi-1" } },
    );
    rerender({ workItemId: "wi-2" });
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-2"]));

    await act(async () => {
      forFirstItem.resolve([proposal("p-1")]);
    });
    expect(ids(result.current.proposals)).toEqual(["p-2"]);
  });

  test("re-reads after a reconnect, recovering a hint it missed", async () => {
    const hub = fakeHub("connected");
    useHubs({ "/hubs/work-item": hub, "/hubs/chat": fakeHub("connected") });
    let onServer = [proposal("p-1")];
    vi.spyOn(authServices.workItemService, "listEditProposals").mockImplementation(
      async () => onServer,
    );

    const { result, rerender } = renderHook(() => useEditProposals({ workItemId: "wi-1" }));
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1"]));

    hub.state = "reconnecting";
    rerender();
    // Decided while the connection was down: the hint for it never arrives.
    onServer = [proposal("p-1", { status: "Rejected", rejectionReason: "No." })];
    hub.state = "connected";
    rerender();

    await waitFor(() => expect(result.current.proposals?.[0]?.status).toBe("Rejected"));
  });
});

describe("useEditProposals for a chat", () => {
  test("joins the chat's hub group before every snapshot read, and leaves it on unmount", async () => {
    const chatHub = fakeHub("connecting");
    useHubs({ "/hubs/work-item": fakeHub("connected"), "/hubs/chat": chatHub });
    const join = deferred<void>();
    chatHub.invoke.mockImplementation((method: unknown) =>
      method === "SubscribeToChat" ? join.promise : Promise.resolve(),
    );
    const list = vi
      .spyOn(authServices.workItemService, "listEditProposalsFor")
      .mockResolvedValue([proposal("p-1", { createdByChatSessionId: "chat-1" })]);

    const { result, rerender, unmount } = renderHook(() =>
      useEditProposals({ chatSessionId: "chat-1" }),
    );
    expect(list).not.toHaveBeenCalled();

    chatHub.state = "connected";
    rerender();
    await waitFor(() => expect(chatHub.invoke).toHaveBeenCalledWith("SubscribeToChat", "chat-1"));
    // Joined but not yet acknowledged: a read now could miss a hint sent in the gap.
    expect(list).not.toHaveBeenCalled();

    await act(async () => {
      join.resolve();
    });
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1"]));
    expect(list).toHaveBeenCalledWith(expect.objectContaining({ chatSessionId: "chat-1" }));

    // A reconnect is a new connection id: join again, then read again.
    const readsBeforeReconnect = list.mock.calls.length;
    chatHub.invoke.mockClear();
    chatHub.invoke.mockImplementation(() => Promise.resolve());
    chatHub.state = "reconnecting";
    rerender();
    chatHub.state = "connected";
    rerender();
    await waitFor(() => expect(list.mock.calls.length).toBeGreaterThan(readsBeforeReconnect));
    const joinIndex = chatHub.invoke.mock.calls.findIndex(
      (c: unknown[]) => c[0] === "SubscribeToChat" && c[1] === "chat-1",
    );
    expect(joinIndex).toBeGreaterThanOrEqual(0);
    expect(chatHub.invoke.mock.invocationCallOrder[joinIndex]).toBeLessThan(
      list.mock.invocationCallOrder[readsBeforeReconnect],
    );

    unmount();
    expect(chatHub.invoke).toHaveBeenCalledWith("UnsubscribeFromChat", "chat-1");
  });

  test("re-reads on a hint for this chat only", async () => {
    const chatHub = fakeHub("connected");
    useHubs({ "/hubs/work-item": fakeHub("connected"), "/hubs/chat": chatHub });
    let onServer = [proposal("p-1")];
    const list = vi
      .spyOn(authServices.workItemService, "listEditProposalsFor")
      .mockImplementation(async () => onServer);

    const { result } = renderHook(() => useEditProposals({ chatSessionId: "chat-1" }));
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1"]));
    const readsBefore = list.mock.calls.length;

    onServer = [proposal("p-1"), proposal("p-2")];
    emit(chatHub, "ChatEditProposalsChanged", { chatSessionId: "chat-other" });
    expect(list).toHaveBeenCalledTimes(readsBefore);

    emit(chatHub, "ChatEditProposalsChanged", { chatSessionId: "chat-1" });
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1", "p-2"]));
  });
});
