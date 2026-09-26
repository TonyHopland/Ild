import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act, waitFor } from "@testing-library/react";
import { useEditProposals } from "./useEditProposals";
import * as signalRHook from "./useSignalR";
import * as authServices from "../services/auth";
import type { WorkItemEditProposal } from "../types";

type ConnectionState = "disconnected" | "connecting" | "connected" | "reconnecting";

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

function chatHub(invoke: (...args: unknown[]) => Promise<void>) {
  const hub = { state: "connected" as ConnectionState, invoke: vi.fn(invoke) };
  vi.spyOn(signalRHook, "useSignalR").mockImplementation(
    () =>
      ({
        connectionState: hub.state,
        on: vi.fn(),
        off: vi.fn(),
        invoke: hub.invoke,
      }) as unknown as ReturnType<typeof signalRHook.useSignalR>,
  );
  return hub;
}

function proposal(id: string, chatSessionId: string): WorkItemEditProposal {
  return {
    id,
    workItemId: "wi-1",
    status: "Pending",
    proposed: { title: id },
    snapshot: {
      title: "Old",
      description: null,
      tags: null,
      branchNameOverride: null,
      baseBranchOverride: null,
    },
    rationale: null,
    rejectionReason: null,
    createdByLoopRunId: null,
    createdByChatSessionId: chatSessionId,
    createdAt: "2026-09-26T10:00:00Z",
    decidedAt: null,
  };
}

const ids = (list: WorkItemEditProposal[] | undefined) => (list ?? []).map((p) => p.id);

afterEach(() => {
  vi.restoreAllMocks();
});

describe("useEditProposals races", () => {
  test("a join for the previous chat that lands late neither reads nor displaces the next chat's read", async () => {
    const firstJoin = deferred<void>();
    chatHub((method, chatId) =>
      method === "SubscribeToChat" && chatId === "chat-1" ? firstJoin.promise : Promise.resolve(),
    );
    const secondRead = deferred<WorkItemEditProposal[]>();
    const list = vi
      .spyOn(authServices.workItemService, "listEditProposalsFor")
      .mockImplementation(({ chatSessionId }) =>
        chatSessionId === "chat-2"
          ? secondRead.promise
          : Promise.resolve([proposal("p-1", "chat-1")]),
      );

    const { result, rerender } = renderHook(
      ({ chatSessionId }: { chatSessionId: string }) => useEditProposals({ chatSessionId }),
      { initialProps: { chatSessionId: "chat-1" } },
    );
    rerender({ chatSessionId: "chat-2" });
    await waitFor(() => expect(list).toHaveBeenCalledWith({ chatSessionId: "chat-2" }));

    await act(async () => {
      firstJoin.resolve();
    });
    expect(list).not.toHaveBeenCalledWith({ chatSessionId: "chat-1" });

    await act(async () => {
      secondRead.resolve([proposal("p-2", "chat-2")]);
    });
    expect(ids(result.current.proposals)).toEqual(["p-2"]);
  });

  test("a refused join skips the read, and the next connect joins and reads", async () => {
    let refuse = true;
    const hub = chatHub((method) =>
      method === "SubscribeToChat" && refuse ? Promise.reject(new Error("no")) : Promise.resolve(),
    );
    vi.spyOn(console, "error").mockImplementation(() => {});
    const list = vi
      .spyOn(authServices.workItemService, "listEditProposalsFor")
      .mockResolvedValue([proposal("p-1", "chat-1")]);

    const { result, rerender } = renderHook(() => useEditProposals({ chatSessionId: "chat-1" }));
    await waitFor(() => expect(hub.invoke).toHaveBeenCalledWith("SubscribeToChat", "chat-1"));
    await act(async () => {});
    expect(list).not.toHaveBeenCalled();

    refuse = false;
    hub.state = "reconnecting";
    rerender();
    hub.state = "connected";
    rerender();

    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1"]));
  });
});
