// @vitest-environment jsdom
import { afterEach, describe, expect, test, vi, type MockInstance } from "vite-plus/test";
import { renderHook, act, waitFor } from "@testing-library/react";
import { useGlobalVariableProposals } from "./useGlobalVariableProposals";
import { useLoopTemplateProposals } from "./useLoopTemplateProposals";
import { usePendingGlobalVariableProposalCount } from "./usePendingGlobalVariableProposalCount";
import { usePendingLoopTemplateProposalCounts } from "./usePendingLoopTemplateProposalCounts";
import * as signalRHook from "./useSignalR";
import { WorkItemHubProvider } from "./useWorkItemHub";
import { useEditProposals } from "./useEditProposals";
import {
  globalVariableService,
  loopTemplateProposalService,
  workItemService,
} from "../services/auth";
import {
  deferred,
  fakeHub,
  makeEditProposal,
  makeGlobalVariableProposal,
  makeLoopTemplateProposal,
  routeHubs,
  type Deferred,
} from "../test-support";
import type { GlobalVariableProposal, LoopTemplateProposal, WorkItemEditProposal } from "../types";

// The list hooks of the proposal kinds (work item edits, global variable
// suggestions, loop template proposals) and the pending-count hooks of the two
// local ones keep one set of rules: join
// the hub group before every snapshot read, on connect and reconnect; re-read
// on a hint for the target; never show a late answer for an older read or an
// earlier target; for counts, the latest read wins and a failed one shows none.

/** Reads the test answers one by one, in any order. */
function heldReads<T>() {
  const reads: Deferred<T>[] = [];
  const read = () => {
    const next = deferred<T>();
    reads.push(next);
    return next.promise;
  };
  return { reads, read };
}

type Proposal = WorkItemEditProposal | GlobalVariableProposal | LoopTemplateProposal;
type Target = { workItemId: string } | { chatSessionId: string };
type ListFilter = { workItemId?: string; chatSessionId?: string };

interface ListKind {
  name: string;
  use: (target: Target) => { proposals: Proposal[] | undefined; refresh: () => void };
  spyList: () => MockInstance<(filter: ListFilter) => Promise<Proposal[]>>;
  /** A proposal of this kind with id `id`. */
  make: (id: string) => Proposal;
  itemHint: string;
  chatHint: string;
  /** What a work item's hint carries. */
  itemPayload: unknown;
}

const listKinds: ListKind[] = [
  {
    name: "work item edit proposals",
    use: (target) => useEditProposals(target),
    // One read per target kind; both land in the one mock the table checks.
    spyList: () => {
      const list = vi.fn<(filter: ListFilter) => Promise<Proposal[]>>();
      vi.spyOn(workItemService, "listEditProposals").mockImplementation(
        async (workItemId) => (await list({ workItemId })) as WorkItemEditProposal[],
      );
      vi.spyOn(workItemService, "listEditProposalsFor").mockImplementation(
        async (filter) => (await list(filter)) as WorkItemEditProposal[],
      );
      return list;
    },
    make: (id) => makeEditProposal({ id }),
    itemHint: "WorkItemEditProposalsChanged",
    chatHint: "ChatEditProposalsChanged",
    itemPayload: { workItemId: "wi-1" },
  },
  {
    name: "global variable suggestions",
    use: (target) => useGlobalVariableProposals(target),
    spyList: () => vi.spyOn(globalVariableService, "listProposals"),
    make: (id) => makeGlobalVariableProposal({ id }),
    itemHint: "GlobalVariableProposalsChanged",
    chatHint: "ChatGlobalVariableProposalsChanged",
    itemPayload: {},
  },
  {
    name: "loop template proposals",
    use: (target) => useLoopTemplateProposals(target),
    spyList: () => vi.spyOn(loopTemplateProposalService, "list"),
    make: (id) => makeLoopTemplateProposal({ id }),
    itemHint: "LoopTemplateProposalsChanged",
    chatHint: "ChatLoopTemplateProposalsChanged",
    itemPayload: { templateId: "tpl-any" },
  },
];

const lt = (id: string) => makeLoopTemplateProposal({ id });
const ids = (list: Proposal[] | undefined) => list?.map((p) => p.id);

afterEach(() => {
  vi.restoreAllMocks();
});

describe.each(listKinds)("the list of $name", (kind) => {
  test("for a work item: joins the board before every read, on connect and on reconnect, and a hint re-reads", async () => {
    const board = fakeHub("connecting");
    routeHubs(signalRHook, { "/hubs/work-item": board, "/hubs/chat": fakeHub("connected") });
    const join = deferred<void>();
    board.invoke.mockImplementation(() => join.promise);
    let onServer: Proposal[] = [kind.make("p-1")];
    const list = kind.spyList().mockImplementation(async () => onServer);

    const { result, rerender } = renderHook(() => kind.use({ workItemId: "wi-1" }), {
      wrapper: WorkItemHubProvider,
    });
    expect(list).not.toHaveBeenCalled();

    board.setState("connected");
    rerender();
    await waitFor(() => expect(board.invoke).toHaveBeenCalledWith("SubscribeToWorkItems"));
    expect(list).not.toHaveBeenCalled();
    await act(async () => join.resolve());
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1"]));
    expect(list).toHaveBeenCalledWith({ workItemId: "wi-1" });

    onServer = [kind.make("p-1"), kind.make("p-2")];
    await board.emit(kind.itemHint, kind.itemPayload);
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1", "p-2"]));

    const rejoin = deferred<void>();
    board.invoke.mockImplementation(() => rejoin.promise);
    board.invoke.mockClear();
    const readsBefore = list.mock.calls.length;
    board.setState("reconnecting");
    rerender();
    onServer = [kind.make("p-3")];
    board.setState("connected");
    rerender();
    await waitFor(() => expect(board.invoke).toHaveBeenCalledWith("SubscribeToWorkItems"));
    expect(list).toHaveBeenCalledTimes(readsBefore);
    await act(async () => rejoin.resolve());
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-3"]));
  });

  test("a join that fails reads nothing", async () => {
    const board = fakeHub("connected");
    routeHubs(signalRHook, { "/hubs/work-item": board, "/hubs/chat": fakeHub("connected") });
    vi.spyOn(console, "error").mockImplementation(() => {});
    board.invoke.mockImplementation(() => Promise.reject(new Error("join refused")));
    const list = kind.spyList().mockResolvedValue([]);

    const { result } = renderHook(() => kind.use({ workItemId: "wi-1" }), {
      wrapper: WorkItemHubProvider,
    });

    await waitFor(() => expect(board.invoke).toHaveBeenCalledWith("SubscribeToWorkItems"));
    await act(async () => {});
    expect(list).not.toHaveBeenCalled();
    expect(result.current.proposals).toBeUndefined();
  });

  test("for a chat: joins its group by id before the read, re-reads on its own hints only, and leaves on unmount", async () => {
    const chatHub = fakeHub("connected");
    routeHubs(signalRHook, { "/hubs/work-item": fakeHub("connected"), "/hubs/chat": chatHub });
    const join = deferred<void>();
    chatHub.invoke.mockImplementation((method: unknown) =>
      method === "SubscribeToChat" ? join.promise : Promise.resolve(),
    );
    let onServer: Proposal[] = [kind.make("p-1")];
    const list = kind.spyList().mockImplementation(async () => onServer);

    const { result, unmount } = renderHook(() => kind.use({ chatSessionId: "chat-1" }), {
      wrapper: WorkItemHubProvider,
    });
    await waitFor(() => expect(chatHub.invoke).toHaveBeenCalledWith("SubscribeToChat", "chat-1"));
    expect(list).not.toHaveBeenCalled();
    await act(async () => join.resolve());
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1"]));
    expect(list).toHaveBeenCalledWith({ chatSessionId: "chat-1" });

    const readsBefore = list.mock.calls.length;
    onServer = [kind.make("p-1"), kind.make("p-2")];
    await chatHub.emit(kind.chatHint, { chatSessionId: "chat-other" });
    expect(list).toHaveBeenCalledTimes(readsBefore);
    await chatHub.emit(kind.chatHint, { chatSessionId: "chat-1" });
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1", "p-2"]));

    unmount();
    expect(chatHub.invoke).toHaveBeenCalledWith("UnsubscribeFromChat", "chat-1");
  });

  test("a late answer to an older read never replaces a newer one", async () => {
    const board = fakeHub("connected");
    routeHubs(signalRHook, { "/hubs/work-item": board, "/hubs/chat": fakeHub("connected") });
    const { reads, read } = heldReads<Proposal[]>();
    kind.spyList().mockImplementation(read);

    const { result } = renderHook(() => kind.use({ workItemId: "wi-1" }), {
      wrapper: WorkItemHubProvider,
    });
    await waitFor(() => expect(reads.length).toBe(1));
    await board.emit(kind.itemHint, kind.itemPayload);
    await waitFor(() => expect(reads.length).toBe(2));

    await act(async () => reads[1].resolve([kind.make("p-new")]));
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-new"]));
    await act(async () => reads[0].resolve([kind.make("p-old")]));
    expect(ids(result.current.proposals)).toEqual(["p-new"]);
  });

  test("a late answer for the previous target is never shown for the next", async () => {
    routeHubs(signalRHook, {
      "/hubs/work-item": fakeHub("connected"),
      "/hubs/chat": fakeHub("connected"),
    });
    const forFirst = deferred<Proposal[]>();
    kind
      .spyList()
      .mockImplementation(async (filter: { workItemId?: string }) =>
        filter.workItemId === "wi-1" ? forFirst.promise : [kind.make("p-2")],
      );

    const { result, rerender } = renderHook(
      ({ workItemId }: { workItemId: string }) => kind.use({ workItemId }),
      { initialProps: { workItemId: "wi-1" }, wrapper: WorkItemHubProvider },
    );
    rerender({ workItemId: "wi-2" });
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-2"]));

    await act(async () => forFirst.resolve([kind.make("p-1")]));
    expect(ids(result.current.proposals)).toEqual(["p-2"]);
  });

  test("a join for the previous chat that lands late neither reads nor displaces the next chat's read", async () => {
    const chatHub = fakeHub("connected");
    routeHubs(signalRHook, { "/hubs/work-item": fakeHub("connected"), "/hubs/chat": chatHub });
    const firstJoin = deferred();
    chatHub.invoke.mockImplementation((method: unknown, chatId: unknown) =>
      method === "SubscribeToChat" && chatId === "chat-1" ? firstJoin.promise : Promise.resolve(),
    );
    const secondRead = deferred<Proposal[]>();
    const list = kind
      .spyList()
      .mockImplementation(async (filter) =>
        filter.chatSessionId === "chat-2" ? secondRead.promise : [kind.make("p-1")],
      );

    const { result, rerender } = renderHook(
      ({ chatSessionId }: { chatSessionId: string }) => kind.use({ chatSessionId }),
      { initialProps: { chatSessionId: "chat-1" }, wrapper: WorkItemHubProvider },
    );
    rerender({ chatSessionId: "chat-2" });
    await waitFor(() => expect(list).toHaveBeenCalledWith({ chatSessionId: "chat-2" }));

    await act(async () => firstJoin.resolve());
    expect(list).not.toHaveBeenCalledWith({ chatSessionId: "chat-1" });

    await act(async () => secondRead.resolve([kind.make("p-2")]));
    expect(ids(result.current.proposals)).toEqual(["p-2"]);
  });
});

describe("the pending loop template proposals of one template", () => {
  test("listen on the provider's hub, asking for no connection of their own, and re-read on that template's hints only", async () => {
    const shared = fakeHub("connected");
    const asked = routeHubs(signalRHook, { "/hubs/work-item": shared });
    let onServer = [lt("p-1")];
    const list = vi
      .spyOn(loopTemplateProposalService, "list")
      .mockImplementation(async () => onServer);

    const { result } = renderHook(() => useLoopTemplateProposals({ templateId: "tpl-1" }), {
      wrapper: WorkItemHubProvider,
    });
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1"]));
    expect(list).toHaveBeenCalledWith({ templateId: "tpl-1", status: "Pending" });
    expect(shared.invoke).toHaveBeenCalledWith("SubscribeToWorkItems");
    expect(asked.filter((url) => url !== null)).toEqual(["/hubs/work-item"]);

    const readsBefore = list.mock.calls.length;
    onServer = [lt("p-1"), lt("p-2")];
    await shared.emit("LoopTemplateProposalsChanged", { templateId: "tpl-other" });
    expect(list).toHaveBeenCalledTimes(readsBefore);
    await shared.emit("LoopTemplateProposalsChanged", { templateId: "tpl-1" });
    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1", "p-2"]));
  });
});

type Count = number | Record<string, number>;

interface CountKind<T extends Count> {
  name: string;
  use: () => T;
  spyRead: () => MockInstance<() => Promise<T>>;
  hint: string;
  hintPayload: unknown;
  some: T;
  more: T;
  none: T;
}

const globalVariableCounts: CountKind<number> = {
  name: "global variable suggestions",
  use: () => usePendingGlobalVariableProposalCount(),
  spyRead: () => vi.spyOn(globalVariableService, "pendingProposalCount"),
  hint: "GlobalVariableProposalsChanged",
  hintPayload: {},
  some: 2,
  more: 5,
  none: 0,
};

const loopTemplateCounts: CountKind<Record<string, number>> = {
  name: "loop template proposals",
  use: () => usePendingLoopTemplateProposalCounts(),
  spyRead: () => vi.spyOn(loopTemplateProposalService, "pendingCounts"),
  hint: "LoopTemplateProposalsChanged",
  hintPayload: { templateId: "tpl-1" },
  some: { "tpl-1": 2 },
  more: { "tpl-1": 5, "tpl-2": 1 },
  none: {},
};

function describePendingCount<T extends Count>(kind: CountKind<T>) {
  describe(`the pending count of ${kind.name}`, () => {
    test("joins the board before every read, on connect and on reconnect, and a hint re-reads", async () => {
      const board = fakeHub("connected");
      routeHubs(signalRHook, { "/hubs/work-item": board });
      const join = deferred<void>();
      board.invoke.mockImplementation(() => join.promise);
      let onServer = kind.some;
      const read = kind.spyRead().mockImplementation(async () => onServer);

      const { result, rerender } = renderHook(() => kind.use(), { wrapper: WorkItemHubProvider });
      expect(result.current).toEqual(kind.none);
      await waitFor(() => expect(board.invoke).toHaveBeenCalledWith("SubscribeToWorkItems"));
      expect(read).not.toHaveBeenCalled();
      await act(async () => join.resolve());
      await waitFor(() => expect(result.current).toEqual(kind.some));

      onServer = kind.more;
      await board.emit(kind.hint, kind.hintPayload);
      await waitFor(() => expect(result.current).toEqual(kind.more));

      const rejoin = deferred<void>();
      board.invoke.mockImplementation(() => rejoin.promise);
      board.invoke.mockClear();
      const readsBefore = read.mock.calls.length;
      board.setState("reconnecting");
      rerender();
      onServer = kind.some;
      board.setState("connected");
      rerender();
      await waitFor(() => expect(board.invoke).toHaveBeenCalledWith("SubscribeToWorkItems"));
      expect(read).toHaveBeenCalledTimes(readsBefore);
      await act(async () => rejoin.resolve());
      await waitFor(() => expect(result.current).toEqual(kind.some));
    });

    test("only the latest read counts, and a failed one shows none rather than an old count", async () => {
      const board = fakeHub("connected");
      routeHubs(signalRHook, { "/hubs/work-item": board });
      vi.spyOn(console, "error").mockImplementation(() => {});
      const { reads, read } = heldReads<T>();
      kind.spyRead().mockImplementation(read);

      const { result } = renderHook(() => kind.use(), { wrapper: WorkItemHubProvider });
      await waitFor(() => expect(reads.length).toBe(1));
      await act(async () => reads[0].resolve(kind.some));
      await waitFor(() => expect(result.current).toEqual(kind.some));

      await board.emit(kind.hint, kind.hintPayload);
      await board.emit(kind.hint, kind.hintPayload);
      await waitFor(() => expect(reads.length).toBe(3));
      await act(async () => reads[2].resolve(kind.more));
      await waitFor(() => expect(result.current).toEqual(kind.more));
      await act(async () => reads[1].resolve(kind.some));
      expect(result.current).toEqual(kind.more);

      await board.emit(kind.hint, kind.hintPayload);
      await waitFor(() => expect(reads.length).toBe(4));
      await act(async () => reads[3].reject(new Error("read failed")));
      await waitFor(() => expect(result.current).toEqual(kind.none));
    });
  });
}

describePendingCount(globalVariableCounts);
describePendingCount(loopTemplateCounts);

describe("the pending loop template proposal counts", () => {
  test("listen on the provider's hub, asking for no connection of their own, and re-read on its hints", async () => {
    const shared = fakeHub("connected");
    const asked = routeHubs(signalRHook, { "/hubs/work-item": shared });
    let onServer: Record<string, number> = { "tpl-1": 1 };
    vi.spyOn(loopTemplateProposalService, "pendingCounts").mockImplementation(async () => onServer);

    const { result } = renderHook(() => usePendingLoopTemplateProposalCounts(), {
      wrapper: WorkItemHubProvider,
    });
    await waitFor(() => expect(result.current).toEqual({ "tpl-1": 1 }));
    expect(shared.invoke).toHaveBeenCalledWith("SubscribeToWorkItems");
    expect(asked.filter((url) => url !== null)).toEqual(["/hubs/work-item"]);

    onServer = { "tpl-1": 3 };
    await shared.emit("LoopTemplateProposalsChanged", { templateId: "tpl-1" });
    await waitFor(() => expect(result.current).toEqual({ "tpl-1": 3 }));
  });
});
