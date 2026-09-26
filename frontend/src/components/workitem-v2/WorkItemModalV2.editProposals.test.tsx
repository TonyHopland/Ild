import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, fireEvent, cleanup, act, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import WorkItemModalV2 from "./WorkItemModalV2";
import { WorkItem, WorkItemEditProposal, WorkItemStatus, WorkItemPriority } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

const MB = 1024 * 1024;

function makeWorkItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Old title",
    description: "The original description.",
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
    dependencyIds: [],
    dependentIds: [],
    attachments: [],
    pendingEditProposalCount: 1,
    ...overrides,
  };
}

function makeProposal(overrides: Partial<WorkItemEditProposal> = {}): WorkItemEditProposal {
  return {
    id: "p-1",
    workItemId: "wi-1",
    status: "Pending",
    proposed: { title: "Agent's sharper title" },
    snapshot: {
      title: "Old title",
      description: "The original description.",
      tags: [],
      branchNameOverride: null,
      baseBranchOverride: null,
    },
    rationale: null,
    rejectionReason: null,
    createdByLoopRunId: "run-1",
    createdByChatSessionId: null,
    createdAt: "2026-09-26T10:00:00Z",
    decidedAt: null,
    ...overrides,
  };
}

function mockServices() {
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn(() => Promise.resolve()),
    connectionState: "connected",
  } as unknown as ReturnType<typeof signalRHook.useSignalR>);
  vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.settingsService, "getAttachmentLimits").mockResolvedValue({
    maxBytesPerFile: 25 * MB,
    maxFilesPerRequest: 10,
    maxTotalBytesPerWorkItem: 250 * MB,
  });
}

async function renderDialog(workItem: WorkItem) {
  const onSave = vi.fn();
  render(
    <MemoryRouter>
      <WorkItemModalV2 workItem={workItem} onClose={vi.fn()} onSave={onSave} />
    </MemoryRouter>,
  );
  await act(async () => {
    await Promise.resolve();
  });
  return { onSave };
}

const overview = () => document.getElementById("wiv2-panel-overview") as HTMLElement;

describe("the detail view's edit proposals", () => {
  test("lists the item's proposals, pending ones with a decision and decided ones with their outcome", async () => {
    mockServices();
    const list = vi.spyOn(authServices.workItemService, "listEditProposals").mockResolvedValue([
      makeProposal(),
      makeProposal({
        id: "p-0",
        status: "Rejected",
        proposed: { description: "An earlier rewrite." },
        rejectionReason: "Loses the repro steps.",
        decidedAt: "2026-09-25T10:00:00Z",
      }),
    ]);

    await renderDialog(makeWorkItem());

    await waitFor(() => expect(overview().textContent).toContain("Agent's sharper title"));
    expect(list).toHaveBeenCalledWith("wi-1");
    expect(overview().textContent).toContain("Loses the repro steps.");
    expect(within(overview()).getAllByRole("button", { name: "Approve" })).toHaveLength(1);
    expect(within(overview()).getAllByRole("button", { name: "Reject" })).toHaveLength(1);
  });

  test("approving re-reads the proposals and shows the proposal approved", async () => {
    mockServices();
    const list = vi
      .spyOn(authServices.workItemService, "listEditProposals")
      .mockResolvedValueOnce([makeProposal()])
      .mockResolvedValue([makeProposal({ status: "Approved", decidedAt: "2026-09-26T11:00:00Z" })]);
    const approve = vi
      .spyOn(authServices.workItemService, "approveEditProposal")
      .mockResolvedValue({
        proposal: makeProposal({ status: "Approved", decidedAt: "2026-09-26T11:00:00Z" }),
        workItem: makeWorkItem({ title: "Agent's sharper title", pendingEditProposalCount: 0 }),
      });

    await renderDialog(makeWorkItem());
    fireEvent.click(await within(overview()).findByRole("button", { name: "Approve" }));

    await waitFor(() =>
      expect(within(overview()).queryByRole("button", { name: "Approve" })).toBeNull(),
    );
    expect(approve).toHaveBeenCalledWith("wi-1", "p-1");
    expect(list.mock.calls.length).toBeGreaterThanOrEqual(2);
    expect(overview().textContent).toContain("Approved");
  });

  test("an approve refused because the item changed shows the proposal stale and leaves the item as it is", async () => {
    mockServices();
    vi.spyOn(authServices.workItemService, "listEditProposals")
      .mockResolvedValueOnce([makeProposal()])
      .mockResolvedValue([makeProposal({ status: "Stale", decidedAt: "2026-09-26T11:00:00Z" })]);
    vi.spyOn(authServices.workItemService, "approveEditProposal").mockRejectedValue({
      status: 409,
      message: "The work item changed after this proposal was made.",
    });

    const { onSave } = await renderDialog(makeWorkItem());
    fireEvent.click(await within(overview()).findByRole("button", { name: "Approve" }));

    await waitFor(() => expect(overview().textContent).toContain("Stale"));
    expect(within(overview()).queryByRole("button", { name: "Approve" })).toBeNull();
    expect(onSave).not.toHaveBeenCalled();
  });
});
