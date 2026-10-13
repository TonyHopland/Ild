import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, cleanup, act, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import WorkItemModalV2 from "./WorkItemModalV2";
import {
  LoopRun,
  RunConversationMessage,
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
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  localStorage.clear();
});

const MB = 1024 * 1024;

function makeWorkItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Old title",
    description: "The original description.",
    status: WorkItemStatus.Done,
    priority: WorkItemPriority.Medium,
    tags: [],
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
    ...overrides,
  };
}

function makeRun(overrides: Partial<LoopRun> = {}): LoopRun {
  return {
    id: "run-1",
    workItemId: "wi-1",
    loopTemplateId: "tmpl-1",
    templateVersion: 1,
    status: LoopRunStatus.Completed,
    currentNodeId: null,
    isPaused: false,
    nodeExecutionCount: 0,
    startedAt: "2026-09-26T09:00:00Z",
    completedAt: null,
    nodes: [],
    ...overrides,
  };
}

function aiTurn(id: number, text: string, runNodeId: string): RunConversationMessage {
  return {
    id,
    runId: "run-1",
    runNodeId,
    role: "ai",
    name: "Coder",
    text,
    timestamp: "2026-09-26T10:00:00Z",
  };
}

/** A loop-made edit proposal card proposing `title`, anchored to the step `runNodeId`. */
function editProposal(
  title: string,
  runNodeId: string | null,
  createdAt: string,
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
    createdAt,
    decidedAt: null,
  };
}

let suggested = 0;

/** A suggestion made by run-1 at step `runNodeId`, told apart by its rationale. */
function suggestion(
  rationale: string,
  runNodeId: string | null,
  createdAt: string,
  workItemId = "wi-1",
) {
  return {
    id: `gp-${rationale}`,
    name: "review_rules",
    value: `Suggested text number ${++suggested}`,
    rationale,
    status: "Pending",
    rejectionReason: null,
    createdAt,
    decidedAt: null,
    snapshotUpdatedAt: "2026-09-01T00:00:00Z",
    currentValue: "Keep reviews short.",
    variableExists: true,
    createdByLoopRunId: "run-1",
    createdByRunNodeId: runNodeId,
    createdByChatSessionId: null,
    chatReplySequence: null,
    source: { kind: "run", workItemId, chatSessionId: null, linkable: true },
  };
}

type Reply = { status: number; body?: unknown };

/**
 * Answers the suggestions listing per work item with `answer`, which may hold
 * its answer back; everything else the dialog fetches past the mocked services fails.
 */
function stubSuggestions(answer: (workItemId: string | null) => Promise<unknown[]> | unknown[]) {
  const asked: (string | null)[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: string, init?: RequestInit) => {
      const url = new URL(input, "http://localhost");
      let reply: Reply = { status: 404, body: { error: "not stubbed" } };
      if (
        (init?.method ?? "GET") === "GET" &&
        url.pathname === "/api/v1/global-variables/proposals"
      ) {
        const workItemId = url.searchParams.get("workItemId");
        asked.push(workItemId);
        reply = { status: 200, body: await answer(workItemId) };
      }
      return {
        ok: reply.status < 400,
        status: reply.status,
        statusText: "",
        text: () => Promise.resolve(JSON.stringify(reply.body)),
      } as Response;
    }),
  );
  return asked;
}

type HubHandler = (msg: { payload: unknown }) => void;

/** Serves `run` and run-1's conversation, `messages`, and `edits` as the item's requested edit proposals. */
function mockServices(
  run: LoopRun | null = null,
  messages: RunConversationMessage[] = [],
  edits: WorkItemEditProposal[] = [],
) {
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
  vi.spyOn(authServices.loopRunService, "getConversation").mockImplementation(
    async (runId: string, after?: number) => {
      const read = runId === "run-1" ? messages.filter((m) => m.id > (after ?? 0)) : [];
      return { runId, messages: read, lastEventId: read[read.length - 1]?.id ?? after ?? null };
    },
  );
  vi.spyOn(authServices.loopRunService, "getEvents").mockResolvedValue({
    entries: [],
    nextCursor: 0,
  } as unknown as Awaited<ReturnType<typeof authServices.loopRunService.getEvents>>);
  vi.spyOn(authServices.workItemService, "listEditProposals").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "listRequestedEditProposals").mockResolvedValue(edits);
  vi.spyOn(authServices.settingsService, "getAttachmentLimits").mockResolvedValue({
    maxBytesPerFile: 25 * MB,
    maxFilesPerRequest: 10,
    maxTotalBytesPerWorkItem: 250 * MB,
  });
  return (event: string, payload: unknown) =>
    act(async () => {
      for (const h of hub[event] ?? []) h({ payload });
    });
}

function dialog(workItem: WorkItem) {
  return (
    <MemoryRouter>
      <WorkItemModalV2 workItem={workItem} onClose={vi.fn()} onSave={vi.fn()} />
    </MemoryRouter>
  );
}

const action = () => document.getElementById("wiv2-panel-action") as HTMLElement;

/** The labels, in the order their elements appear in the Action tab. */
function documentOrder(labels: string[]): string[] {
  return labels
    .map((l) => [l, within(action()).getByText(l, { exact: false })] as const)
    .sort(([, a], [, b]) =>
      a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1,
    )
    .map(([l]) => l);
}

describe("global variable suggestions in the Action tab", () => {
  test("the item's runs' suggestions sit after the turn that made them, in time order with its edit proposals, and one without a step closes the thread", async () => {
    mockServices(
      makeRun(),
      [aiTurn(1, "Planned it.", "exec-1"), aiTurn(2, "Coded it.", "exec-2")],
      [editProposal("Edit proposal title", "exec-1", "2026-09-26T10:00:00Z")],
    );
    const asked = stubSuggestions((workItemId) =>
      workItemId === "wi-1"
        ? [
            suggestion("Loose suggestion", null, "2026-09-26T08:00:00Z"),
            suggestion("Later at step one", "exec-1", "2026-09-26T10:05:00Z"),
            suggestion("At step two", "exec-2", "2026-09-26T10:30:00Z"),
            suggestion("Earlier at step one", "exec-1", "2026-09-26T09:55:00Z"),
          ]
        : [],
    );

    render(dialog(makeWorkItem({ latestLoopRunId: "run-1" })));

    await waitFor(() => expect(action().textContent).toContain("Loose suggestion"));
    await waitFor(() => expect(action().textContent).toContain("Coded it."));
    await waitFor(() => expect(action().textContent).toContain("Edit proposal title"));
    expect(asked).toContain("wi-1");
    const expected = [
      "Planned it.",
      "Earlier at step one",
      "Edit proposal title",
      "Later at step one",
      "Coded it.",
      "At step two",
      "Loose suggestion",
    ];
    expect(documentOrder(expected)).toEqual(expected);
    expect(action().textContent).toContain("review_rules");
  });

  test("a hint that suggestions changed brings in a new card without a reload", async () => {
    const hint = mockServices();
    let served: unknown[] = [];
    stubSuggestions(() => served);
    render(dialog(makeWorkItem()));
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    expect(action().textContent).not.toContain("Made just now");

    served = [suggestion("Made just now", null, "2026-09-26T10:00:00Z")];
    await hint("GlobalVariableProposalsChanged", { name: "review_rules" });

    await waitFor(() => expect(action().textContent).toContain("Made just now"));
  });

  test("after switching items, a late answer for the old item never shows its cards", async () => {
    mockServices();
    let answerFirstItem: (cards: unknown[]) => void = () => {};
    const asked = stubSuggestions((workItemId) =>
      workItemId === "wi-1"
        ? new Promise<unknown[]>((resolve) => {
            answerFirstItem = resolve;
          })
        : [suggestion("Second item's suggestion", null, "2026-09-26T10:00:00Z", "wi-2")],
    );
    const view = render(dialog(makeWorkItem()));
    await waitFor(() => expect(asked).toContain("wi-1"));

    view.rerender(dialog(makeWorkItem({ id: "wi-2" })));
    await waitFor(() => expect(action().textContent).toContain("Second item's suggestion"));
    await act(async () => {
      answerFirstItem([suggestion("First item's suggestion", null, "2026-09-26T10:00:00Z")]);
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    expect(action().textContent).not.toContain("First item's suggestion");
    expect(action().textContent).toContain("Second item's suggestion");
  });
});
