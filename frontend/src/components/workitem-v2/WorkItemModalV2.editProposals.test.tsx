import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, fireEvent, cleanup, act, waitFor, within, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import WorkItemModalV2 from "./WorkItemModalV2";
import {
  ConversationMessage,
  LoopRun,
  LoopRunStatus,
  WorkItem,
  WorkItemEditProposal,
  WorkItemStatus,
  WorkItemPriority,
} from "../../types";
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

function makeRun(overrides: Partial<LoopRun> = {}): LoopRun {
  return {
    id: "run-1",
    workItemId: "wi-1",
    loopTemplateId: "tmpl-1",
    templateVersion: 1,
    status: LoopRunStatus.Running,
    currentNodeId: null,
    isPaused: false,
    nodeExecutionCount: 0,
    startedAt: "2026-09-26T09:00:00Z",
    completedAt: null,
    nodes: [],
    ...overrides,
  };
}

/** A loop-made card proposing `title`, anchored to the step `runNodeId`. */
function makeProposal(
  title: string,
  runNodeId: string | null,
  overrides: Partial<WorkItemEditProposal> = {},
): WorkItemEditProposal {
  return {
    id: `p-${title}`,
    workItemId: "wi-1",
    status: "Pending",
    proposed: { title },
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
    createdByRunNodeId: runNodeId,
    chatReplySequence: null,
    createdAt: "2026-09-26T10:00:00Z",
    decidedAt: null,
    ...overrides,
  };
}

function aiTurn(content: string, runNodeId: string, timestamp: string): ConversationMessage {
  return { role: "ai", content, timestamp, name: "Coder", runNodeId };
}

type HubHandler = (msg: { payload: unknown }) => void;

function mockServices(run: LoopRun | null = null) {
  const hub: Record<string, HubHandler[]> = {};
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: (event: string, handler: HubHandler) => {
      (hub[event] ??= []).push(handler);
    },
    off: (event: string, handler: HubHandler) => {
      hub[event] = (hub[event] ?? []).filter((h) => h !== handler);
    },
    invoke: vi.fn(() => Promise.resolve()),
    connectionState: "connected",
  } as unknown as ReturnType<typeof signalRHook.useSignalR>);
  vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue(run ? [run] : []);
  vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getTurnVariables").mockResolvedValue([]);
  if (run) vi.spyOn(authServices.loopRunService, "getById").mockResolvedValue(run);
  vi.spyOn(authServices.loopRunService, "getEvents").mockResolvedValue({
    entries: [],
    nextCursor: 0,
  } as unknown as Awaited<ReturnType<typeof authServices.loopRunService.getEvents>>);
  vi.spyOn(authServices.workItemService, "listEditProposals").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "listRequestedEditProposals").mockResolvedValue([]);
  vi.spyOn(authServices.settingsService, "getAttachmentLimits").mockResolvedValue({
    maxBytesPerFile: 25 * MB,
    maxFilesPerRequest: 10,
    maxTotalBytesPerWorkItem: 250 * MB,
  });
  const hint = (workItemId: string) =>
    act(() => {
      for (const h of hub["WorkItemEditProposalsChanged"] ?? []) h({ payload: { workItemId } });
    });
  return { hint };
}

function dialog(workItem: WorkItem, onSave = vi.fn()) {
  return (
    <MemoryRouter>
      <WorkItemModalV2 workItem={workItem} onClose={vi.fn()} onSave={onSave} />
    </MemoryRouter>
  );
}

async function renderDialog(workItem: WorkItem) {
  const onSave = vi.fn();
  const view = render(dialog(workItem, onSave));
  await act(async () => {
    await Promise.resolve();
  });
  return { onSave, rerender: view.rerender };
}

const overview = () => document.getElementById("wiv2-panel-overview") as HTMLElement;
const action = () => document.getElementById("wiv2-panel-action") as HTMLElement;
const cardOf = (title: string) =>
  within(action()).getByText(title).closest(".edit-proposal-card") as HTMLElement;

/** The labels, in the order their elements appear in the document. */
function documentOrder(entries: [string, Element][]): string[] {
  return [...entries]
    .sort(([, a], [, b]) =>
      a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1,
    )
    .map(([label]) => label);
}

function inAction(labels: string[]): [string, Element][] {
  return labels.map((l) => [
    l,
    l === "PR details"
      ? within(action()).getByRole("button", { name: /PR details/ })
      : within(action()).getByText(l),
  ]);
}

describe("edit proposals in the detail view", () => {
  test("the Overview shows no card, and the Action tab shows the loop's cards but not a chat's", async () => {
    mockServices();
    vi.spyOn(authServices.workItemService, "listRequestedEditProposals").mockResolvedValue([
      makeProposal("Loop title", null),
      makeProposal("Chat title", null, {
        createdByLoopRunId: null,
        createdByChatSessionId: "chat-1",
        chatReplySequence: 1,
      }),
    ]);

    await renderDialog(makeWorkItem({ status: WorkItemStatus.Done }));

    await waitFor(() => expect(action().textContent).toContain("Loop title"));
    expect(action().textContent).not.toContain("Chat title");
    expect(within(action()).queryByText("No action required.")).toBeNull();
    expect(overview().textContent).not.toContain("Loop title");
    expect(overview().textContent).not.toContain("Chat title");
    expect(within(overview()).queryByRole("button", { name: "Approve" })).toBeNull();
  });

  test("a card follows its step's last turn, sits under the live bubble while its step has none, and goes last without a step", async () => {
    mockServices(makeRun());
    vi.spyOn(authServices.workItemService, "listRequestedEditProposals").mockResolvedValue([
      makeProposal("End second", null, { createdAt: "2026-09-26T09:30:00Z" }),
      makeProposal("Live card", "exec-3", { createdAt: "2026-09-26T10:40:00Z" }),
      makeProposal("Step two card", "exec-2", { createdAt: "2026-09-26T10:30:00Z" }),
      makeProposal("Step one third", "exec-1", { id: "p-b", createdAt: "2026-09-26T10:05:00Z" }),
      makeProposal("Step one second", "exec-1", { id: "p-a", createdAt: "2026-09-26T10:05:00Z" }),
      makeProposal("Step one first", "exec-1", { createdAt: "2026-09-26T09:59:00Z" }),
      makeProposal("End first", null, { createdAt: "2026-09-26T09:00:00Z" }),
    ]);

    await renderDialog(
      makeWorkItem({
        status: WorkItemStatus.Running,
        currentLoopRunId: "run-1",
        prUrl: "https://git.example/pr/1",
        conversation: [
          aiTurn("Planning.", "exec-1", "2026-09-26T10:00:00Z"),
          aiTurn("Plan written.", "exec-1", "2026-09-26T10:10:00Z"),
          { role: "human", content: "Go ahead.", timestamp: "2026-09-26T10:20:00Z", name: null },
          aiTurn("Coded it.", "exec-2", "2026-09-26T10:35:00Z"),
        ],
      }),
    );
    await waitFor(() => expect(action().textContent).toContain("Live card"));

    const expected = [
      "Planning.",
      "Plan written.",
      "Step one first",
      "Step one second",
      "Step one third",
      "Go ahead.",
      "Coded it.",
      "Step two card",
      "Live Output",
      "Live card",
      "PR details",
      "End first",
      "End second",
    ];
    expect(documentOrder(inAction(expected))).toEqual(expected);
  });

  test("while the item waits on a human, a card whose step wrote no turn sits above the feedback card and one without a step below it", async () => {
    mockServices(makeRun({ status: LoopRunStatus.WaitingHuman }));
    vi.spyOn(authServices.workItemService, "listRequestedEditProposals").mockResolvedValue([
      makeProposal("Loose card", null),
      makeProposal("Halted step card", "exec-9"),
      makeProposal("Step card", "exec-1"),
    ]);

    await renderDialog(
      makeWorkItem({
        status: WorkItemStatus.HumanFeedback,
        humanFeedbackReason: "Human Input Needed",
        currentLoopRunId: "run-1",
        conversation: [aiTurn("Done.", "exec-1", "2026-09-26T10:00:00Z")],
      }),
    );
    await waitFor(() => expect(action().textContent).toContain("Loose card"));

    const expected = ["Done.", "Step card", "Halted step card", "Human Feedback", "Loose card"];
    expect(documentOrder(inAction(expected))).toEqual(expected);
  });

  test("approving re-reads the cards and the approved card stays where it was", async () => {
    mockServices();
    const first = makeProposal("Agent's sharper title", "exec-1");
    const second = makeProposal("Agent's later title", "exec-2");
    const list = vi
      .spyOn(authServices.workItemService, "listRequestedEditProposals")
      .mockResolvedValueOnce([second, first])
      .mockResolvedValue([
        second,
        { ...first, status: "Approved", decidedAt: "2026-09-26T11:00:00Z" },
      ]);
    const approve = vi
      .spyOn(authServices.workItemService, "approveEditProposal")
      .mockResolvedValue({
        proposal: { ...first, status: "Approved", decidedAt: "2026-09-26T11:00:00Z" },
        workItem: makeWorkItem({ title: "Agent's sharper title", pendingEditProposalCount: 0 }),
      });
    await renderDialog(
      makeWorkItem({
        status: WorkItemStatus.Done,
        conversation: [
          aiTurn("First turn.", "exec-1", "2026-09-26T10:00:00Z"),
          aiTurn("Second turn.", "exec-2", "2026-09-26T10:10:00Z"),
        ],
      }),
    );
    const expected = [
      "First turn.",
      "Agent's sharper title",
      "Second turn.",
      "Agent's later title",
    ];
    await waitFor(() => expect(action().textContent).toContain("Agent's sharper title"));
    fireEvent.click(screen.getByRole("tab", { name: /Action/ }));

    fireEvent.click(
      within(cardOf("Agent's sharper title")).getByRole("button", { name: "Approve" }),
    );

    await waitFor(() =>
      expect(
        within(cardOf("Agent's sharper title")).queryByRole("button", { name: "Approve" }),
      ).toBeNull(),
    );
    expect(approve).toHaveBeenCalledWith("wi-1", first.id);
    expect(list).toHaveBeenCalledWith("wi-1");
    expect(list.mock.calls.length).toBeGreaterThanOrEqual(2);
    expect(cardOf("Agent's sharper title").textContent).toContain("Approved");
    expect(documentOrder(inAction(expected))).toEqual(expected);
  });

  test("an approve refused because the item changed shows the card stale, in place, and leaves the item as it is", async () => {
    mockServices();
    const card = makeProposal("Agent's sharper title", "exec-1");
    vi.spyOn(authServices.workItemService, "listRequestedEditProposals")
      .mockResolvedValueOnce([card])
      .mockResolvedValue([{ ...card, status: "Stale", decidedAt: "2026-09-26T11:00:00Z" }]);
    vi.spyOn(authServices.workItemService, "approveEditProposal").mockRejectedValue({
      status: 409,
      message: "The work item changed after this proposal was made.",
    });
    const { onSave } = await renderDialog(
      makeWorkItem({
        status: WorkItemStatus.Done,
        conversation: [
          aiTurn("First turn.", "exec-1", "2026-09-26T10:00:00Z"),
          aiTurn("Second turn.", "exec-2", "2026-09-26T10:10:00Z"),
        ],
      }),
    );
    await waitFor(() => expect(action().textContent).toContain("Agent's sharper title"));
    fireEvent.click(screen.getByRole("tab", { name: /Action/ }));

    fireEvent.click(
      within(cardOf("Agent's sharper title")).getByRole("button", { name: "Approve" }),
    );

    await waitFor(() => expect(cardOf("Agent's sharper title").textContent).toContain("Stale"));
    expect(within(action()).queryByRole("button", { name: "Approve" })).toBeNull();
    expect(onSave).not.toHaveBeenCalled();
    const expected = ["First turn.", "Agent's sharper title", "Second turn."];
    expect(documentOrder(inAction(expected))).toEqual(expected);
  });

  test("a hint for the item brings in its new card, and after switching items the old item's cards are gone", async () => {
    const { hint } = mockServices();
    const served: Record<string, WorkItemEditProposal[]> = { "wi-1": [] };
    let answerSecondItem: (cards: WorkItemEditProposal[]) => void = () => {};
    const list = vi
      .spyOn(authServices.workItemService, "listRequestedEditProposals")
      .mockImplementation((id: string) =>
        id === "wi-2"
          ? new Promise((resolve) => {
              answerSecondItem = resolve;
            })
          : Promise.resolve(served[id] ?? []),
      );
    const { rerender } = await renderDialog(makeWorkItem({ status: WorkItemStatus.Done }));
    await waitFor(() => expect(list).toHaveBeenCalledWith("wi-1"));
    const readsBefore = list.mock.calls.length;

    served["wi-1"] = [makeProposal("First item's card", null)];
    hint("wi-other");
    expect(list.mock.calls.length).toBe(readsBefore);
    hint("wi-1");

    await waitFor(() => expect(action().textContent).toContain("First item's card"));

    rerender(dialog(makeWorkItem({ id: "wi-2", status: WorkItemStatus.Done })));
    await waitFor(() => expect(list).toHaveBeenCalledWith("wi-2"));

    expect(action().textContent).not.toContain("First item's card");
    await act(async () => {
      answerSecondItem([
        makeProposal("Second item's card", null, { id: "p-2", workItemId: "wi-2" }),
      ]);
    });
    await waitFor(() => expect(action().textContent).toContain("Second item's card"));
    expect(action().textContent).not.toContain("First item's card");
  });

  test("a card keeps a reason being typed when its step's turn arrives and it moves under it", async () => {
    mockServices(makeRun());
    vi.spyOn(authServices.workItemService, "listRequestedEditProposals").mockResolvedValue([
      makeProposal("Live card", "exec-2"),
    ]);
    const running = (conversation: ConversationMessage[]) =>
      makeWorkItem({ status: WorkItemStatus.Running, currentLoopRunId: "run-1", conversation });
    const { rerender } = await renderDialog(
      running([aiTurn("Planned.", "exec-1", "2026-09-26T10:00:00Z")]),
    );
    await waitFor(() => expect(action().textContent).toContain("Live card"));
    fireEvent.click(within(cardOf("Live card")).getByRole("button", { name: "Reject" }));
    fireEvent.change(within(action()).getByLabelText("Rejection reason (optional)"), {
      target: { value: "Too long" },
    });

    rerender(
      dialog(
        running([
          aiTurn("Planned.", "exec-1", "2026-09-26T10:00:00Z"),
          aiTurn("Coded it.", "exec-2", "2026-09-26T10:30:00Z"),
        ]),
      ),
    );

    const expected = ["Planned.", "Coded it.", "Live card", "Live Output"];
    expect(documentOrder(inAction(expected))).toEqual(expected);
    expect(
      (within(action()).getByLabelText("Rejection reason (optional)") as HTMLTextAreaElement).value,
    ).toBe("Too long");
  });

  test("a card another item's run proposed for this item is not in this item's Action tab but in the requester's, and approving it there decides it on this item", async () => {
    mockServices();
    const forB = makeProposal("A's title for B", "exec-a", {
      id: "p-ab",
      workItemId: "wi-2",
      createdByLoopRunId: "run-a",
      requestedByWorkItemId: "wi-1",
    });
    let decided = false;
    const current = () =>
      decided ? { ...forB, status: "Approved" as const, decidedAt: "2026-09-26T11:00:00Z" } : forB;
    vi.spyOn(authServices.workItemService, "listEditProposals").mockImplementation((id: string) =>
      Promise.resolve(id === "wi-2" ? [current()] : []),
    );
    const requested = vi
      .spyOn(authServices.workItemService, "listRequestedEditProposals")
      .mockImplementation((id: string) => Promise.resolve(id === "wi-1" ? [current()] : []));
    const approve = vi
      .spyOn(authServices.workItemService, "approveEditProposal")
      .mockImplementation(async () => {
        decided = true;
        return {
          proposal: current(),
          workItem: makeWorkItem({
            id: "wi-2",
            title: "A's title for B",
            pendingEditProposalCount: 0,
          }),
        };
      });

    const { rerender } = await renderDialog(
      makeWorkItem({ id: "wi-2", title: "Item B", status: WorkItemStatus.Done }),
    );
    await waitFor(() =>
      expect(authServices.workItemService.listEditProposals).toHaveBeenCalledWith("wi-2"),
    );
    await act(async () => {
      await Promise.resolve();
    });
    fireEvent.click(screen.getByRole("tab", { name: /Action/ }));
    expect(action().textContent).not.toContain("A's title for B");
    expect(within(action()).queryByRole("button", { name: "Approve" })).toBeNull();

    rerender(dialog(makeWorkItem({ id: "wi-1", title: "Item A", status: WorkItemStatus.Done })));
    await waitFor(() => expect(action().textContent).toContain("A's title for B"));
    fireEvent.click(screen.getByRole("tab", { name: /Action/ }));
    const readsBefore = requested.mock.calls.filter(([id]) => id === "wi-1").length;

    fireEvent.click(within(cardOf("A's title for B")).getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(cardOf("A's title for B").textContent).toContain("Approved"));
    expect(approve).toHaveBeenCalledWith("wi-2", "p-ab");
    expect(requested.mock.calls.filter(([id]) => id === "wi-1").length).toBeGreaterThan(
      readsBefore,
    );
    expect(within(action()).queryByRole("button", { name: "Approve" })).toBeNull();
  });
});

describe("pending proposals on the Overview", () => {
  const heading = () => within(overview()).queryByRole("heading", { name: /propos/i });
  const rows = () => within(overview()).queryAllByRole("listitem");
  const rowWith = (field: string) => {
    const matching = rows().filter((r) => r.textContent?.includes(field));
    expect(matching).toHaveLength(1);
    return matching[0];
  };

  const fromItemA = makeProposal("From A", null, {
    id: "p-a",
    workItemId: "wi-2",
    createdByLoopRunId: "run-a",
    requestedByWorkItemId: "wi-1",
    proposed: { title: "From A", tags: ["x"] },
  });
  const fromChat = makeProposal("From chat", null, {
    id: "p-c",
    workItemId: "wi-2",
    createdByLoopRunId: null,
    createdByChatSessionId: "chat-1",
    chatReplySequence: 1,
    requestedByWorkItemId: null,
    proposed: { description: "From chat" },
  });
  const fromGoneRun = makeProposal("Orphan", null, {
    id: "p-o",
    workItemId: "wi-2",
    createdByLoopRunId: "run-gone",
    requestedByWorkItemId: null,
    proposed: { branchNameOverride: "feature/orphan" },
  });
  const decidedFromItemC = makeProposal("Decided", null, {
    id: "p-d",
    workItemId: "wi-2",
    status: "Approved",
    decidedAt: "2026-09-26T11:00:00Z",
    createdByLoopRunId: "run-c",
    requestedByWorkItemId: "wi-3",
    proposed: { baseBranchOverride: "main" },
  });

  test("lists each pending proposal with its source, links a loop's to the requesting item, and offers no decision", async () => {
    mockServices();
    vi.spyOn(authServices.workItemService, "listEditProposals").mockResolvedValue([
      fromItemA,
      fromChat,
      fromGoneRun,
      decidedFromItemC,
    ]);

    await renderDialog(makeWorkItem({ id: "wi-2", status: WorkItemStatus.Done }));

    await waitFor(() => expect(heading()).not.toBeNull());
    expect(rows()).toHaveLength(3);

    const loopRow = rowWith("Title");
    expect(loopRow.textContent).toContain("Tags");
    expect(within(loopRow).getByRole("link", { name: "#wi-1" }).getAttribute("href")).toBe(
      "/taskboard/wi-1",
    );

    const chatRow = rowWith("Description");
    expect(chatRow.textContent).toContain("Chat");
    expect(within(chatRow).queryByRole("link")).toBeNull();

    const orphanRow = rowWith("Branch");
    expect(orphanRow.textContent).not.toContain("Chat");
    expect(within(orphanRow).queryByRole("link")).toBeNull();

    expect(rows().some((r) => r.textContent?.includes("Base branch"))).toBe(false);
    expect(within(overview()).queryByRole("link", { name: "#wi-3" })).toBeNull();
    expect(within(overview()).queryByRole("button", { name: "Approve" })).toBeNull();
    expect(within(overview()).queryByRole("button", { name: "Reject" })).toBeNull();
  });

  test("shows only while the item has a pending proposal, follows hints, and never shows the previous item's rows", async () => {
    const { hint } = mockServices();
    const served: Record<string, WorkItemEditProposal[]> = { "wi-2": [decidedFromItemC] };
    let answerThirdItem: (cards: WorkItemEditProposal[]) => void = () => {};
    const list = vi
      .spyOn(authServices.workItemService, "listEditProposals")
      .mockImplementation((id: string) =>
        id === "wi-3"
          ? new Promise((resolve) => {
              answerThirdItem = resolve;
            })
          : Promise.resolve(served[id] ?? []),
      );

    const { rerender } = await renderDialog(
      makeWorkItem({ id: "wi-2", status: WorkItemStatus.Done }),
    );
    await waitFor(() => expect(list).toHaveBeenCalledWith("wi-2"));
    await act(async () => {
      await Promise.resolve();
    });
    expect(heading()).toBeNull();
    expect(rows()).toHaveLength(0);

    served["wi-2"] = [fromItemA, decidedFromItemC];
    hint("wi-2");
    await waitFor(() => expect(rows()).toHaveLength(1));
    expect(within(overview()).getByRole("link", { name: "#wi-1" })).toBeTruthy();

    rerender(dialog(makeWorkItem({ id: "wi-3", status: WorkItemStatus.Done })));
    await waitFor(() => expect(list).toHaveBeenCalledWith("wi-3"));
    expect(heading()).toBeNull();
    expect(rows()).toHaveLength(0);

    await act(async () => {
      answerThirdItem([{ ...fromChat, id: "p-c3", workItemId: "wi-3" }]);
    });
    await waitFor(() => expect(rows()).toHaveLength(1));
    expect(rowWith("Description").textContent).toContain("Chat");
    expect(within(overview()).queryByRole("link", { name: "#wi-1" })).toBeNull();
  });
});
