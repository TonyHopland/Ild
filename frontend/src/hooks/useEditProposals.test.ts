// @vitest-environment jsdom
import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act, waitFor } from "@testing-library/react";
import { useEditProposals } from "./useEditProposals";
import * as signalRHook from "./useSignalR";
import * as authServices from "../services/auth";
import type { WorkItemEditProposal } from "../types";
import { WorkItemHubProvider } from "./useWorkItemHub";
import { fakeHub, routeHubs } from "../test-support";

function emit(hub: ReturnType<typeof fakeHub>, event: string, payload: unknown) {
  act(() => {
    hub.dispatch(event, payload);
  });
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
    routeHubs(signalRHook, { "/hubs/work-item": hub, "/hubs/chat": fakeHub("connected") });
    let onServer = [proposal("p-1")];
    const list = vi
      .spyOn(authServices.workItemService, "listEditProposals")
      .mockImplementation(async () => onServer);

    const { result } = renderHook(() => useEditProposals({ workItemId: "wi-1" }), {
      wrapper: WorkItemHubProvider,
    });

    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1"]));
    expect(list).toHaveBeenCalledWith("wi-1");
    const readsAfterMount = list.mock.calls.length;

    onServer = [proposal("p-1", { status: "Approved" })];
    emit(hub, "WorkItemEditProposalsChanged", { workItemId: "wi-other" });
    expect(list).toHaveBeenCalledTimes(readsAfterMount);

    emit(hub, "WorkItemEditProposalsChanged", { workItemId: "wi-1" });
    await waitFor(() => expect(result.current.proposals?.[0]?.status).toBe("Approved"));
  });
});

describe("useEditProposals for a requesting work item", () => {
  test("reads what the item's runs proposed, joins the work-item hub first, and re-reads on a work-item hint for it only", async () => {
    const itemHub = fakeHub("connected");
    const chatHub = fakeHub("connected");
    routeHubs(signalRHook, { "/hubs/work-item": itemHub, "/hubs/chat": chatHub });
    let onServer = [proposal("p-1", { workItemId: "wi-2", createdByLoopRunId: "run-a" })];
    const list = vi
      .spyOn(authServices.workItemService, "listRequestedEditProposals")
      .mockImplementation(async () => onServer);
    const byTarget = vi
      .spyOn(authServices.workItemService, "listEditProposals")
      .mockResolvedValue([]);

    const { result } = renderHook(() => useEditProposals({ requestedByWorkItemId: "wi-1" }), {
      wrapper: WorkItemHubProvider,
    });

    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1"]));
    expect(list).toHaveBeenCalledWith("wi-1");
    expect(byTarget).not.toHaveBeenCalled();
    expect(itemHub.invoke).toHaveBeenCalledWith("SubscribeToWorkItems");
    const readsAfterMount = list.mock.calls.length;

    onServer = [proposal("p-1", { workItemId: "wi-2", status: "Stale" })];
    emit(itemHub, "WorkItemEditProposalsChanged", { workItemId: "wi-2" });
    emit(chatHub, "ChatEditProposalsChanged", { chatSessionId: "wi-1" });
    expect(list).toHaveBeenCalledTimes(readsAfterMount);

    emit(itemHub, "WorkItemEditProposalsChanged", { workItemId: "wi-1" });
    await waitFor(() => expect(result.current.proposals?.[0]?.status).toBe("Stale"));
  });
});
