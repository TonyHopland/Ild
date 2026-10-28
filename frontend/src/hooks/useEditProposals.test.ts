// @vitest-environment jsdom
import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, waitFor } from "@testing-library/react";
import { useEditProposals } from "./useEditProposals";
import * as signalRHook from "./useSignalR";
import * as authServices from "../services/auth";
import type { WorkItemEditProposal } from "../types";
import { WorkItemHubProvider } from "./useWorkItemHub";
import { fakeHub, makeEditProposal, routeHubs } from "../test-support";

// What every proposal list shares (joins, reads, chat hints, late answers) is in
// proposalHooks.test.ts; this is what only edit proposals do.

const ids = (list: WorkItemEditProposal[] | undefined) => (list ?? []).map((p) => p.id);

afterEach(() => {
  vi.restoreAllMocks();
});

describe("useEditProposals for a work item", () => {
  test("re-reads on a hint for that item only", async () => {
    const hub = fakeHub("connected");
    routeHubs(signalRHook, { "/hubs/work-item": hub, "/hubs/chat": fakeHub("connected") });
    let onServer = [makeEditProposal()];
    const list = vi
      .spyOn(authServices.workItemService, "listEditProposals")
      .mockImplementation(async () => onServer);

    const { result } = renderHook(() => useEditProposals({ workItemId: "wi-1" }), {
      wrapper: WorkItemHubProvider,
    });

    await waitFor(() => expect(ids(result.current.proposals)).toEqual(["p-1"]));
    const readsAfterMount = list.mock.calls.length;

    onServer = [makeEditProposal({ status: "Approved" })];
    await hub.emit("WorkItemEditProposalsChanged", { workItemId: "wi-other" });
    expect(list).toHaveBeenCalledTimes(readsAfterMount);

    await hub.emit("WorkItemEditProposalsChanged", { workItemId: "wi-1" });
    await waitFor(() => expect(result.current.proposals?.[0]?.status).toBe("Approved"));
  });
});

describe("useEditProposals for a requesting work item", () => {
  test("reads what the item's runs proposed, joins the work-item hub first, and re-reads on a work-item hint for it only", async () => {
    const itemHub = fakeHub("connected");
    const chatHub = fakeHub("connected");
    routeHubs(signalRHook, { "/hubs/work-item": itemHub, "/hubs/chat": chatHub });
    let onServer = [makeEditProposal({ workItemId: "wi-2", createdByLoopRunId: "run-a" })];
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

    onServer = [makeEditProposal({ workItemId: "wi-2", status: "Stale" })];
    await itemHub.emit("WorkItemEditProposalsChanged", { workItemId: "wi-2" });
    await chatHub.emit("ChatEditProposalsChanged", { chatSessionId: "wi-1" });
    expect(list).toHaveBeenCalledTimes(readsAfterMount);

    await itemHub.emit("WorkItemEditProposalsChanged", { workItemId: "wi-1" });
    await waitFor(() => expect(result.current.proposals?.[0]?.status).toBe("Stale"));
  });
});
