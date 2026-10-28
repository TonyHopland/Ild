import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, cleanup, act, waitFor, within, fireEvent, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import WorkItemModalV2 from "./WorkItemModalV2";
import {
  LoopRun,
  RunConversationMessage,
  LoopRunStatus,
  WorkItem,
  WorkItemStatus,
} from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";
import {
  documentOrder,
  fakeHub,
  makeLoopTemplateProposal,
  reply,
  stubFetch,
  routeEveryHub,
} from "../../test-support";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";

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
    priority: 0,
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

function makeRun(): LoopRun {
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

/** A proposal made by run-1 at step `runNodeId`, told apart by its rationale. */
function proposal(rationale: string, runNodeId: string | null, createdAt: string) {
  return makeLoopTemplateProposal({
    id: `lp-${rationale}`,
    rationale,
    createdAt,
    createdByRunNodeId: runNodeId,
  });
}

/**
 * Answers the loop template proposal listing per work item with `answer`;
 * everything else the dialog fetches past the mocked services fails.
 */
function stubProposals(answer: (workItemId: string | null) => unknown[]) {
  const asked: (string | null)[] = [];
  stubFetch((call) => {
    if (call.method !== "GET" || call.path !== "/api/v1/loop-template-proposals")
      return reply(404, { error: "not stubbed" });
    const workItemId = call.query.get("workItemId");
    asked.push(workItemId);
    return reply(200, answer(workItemId));
  });
  return asked;
}

/** Serves `run` and run-1's conversation `messages`. */
function mockServices(run: LoopRun | null = null, messages: RunConversationMessage[] = []) {
  const hub = fakeHub();
  routeEveryHub(signalRHook, hub);
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
  vi.spyOn(authServices.workItemService, "listRequestedEditProposals").mockResolvedValue([]);
  vi.spyOn(authServices.settingsService, "getAttachmentLimits").mockResolvedValue({
    maxBytesPerFile: 25 * MB,
    maxFilesPerRequest: 10,
    maxTotalBytesPerWorkItem: 250 * MB,
  });
  return hub.emit;
}

function dialog(workItem: WorkItem) {
  return (
    <MemoryRouter>
      <WorkItemModalV2 workItem={workItem} onClose={vi.fn()} onSave={vi.fn()} />
    </MemoryRouter>
  );
}

const action = () => document.getElementById("wiv2-panel-action") as HTMLElement;

describe("loop template proposals in the Action tab", () => {
  test("the item's runs' proposals sit after the turn that made them, as cards a human decides", async () => {
    mockServices(makeRun(), [aiTurn(1, "Planned it.", "exec-1"), aiTurn(2, "Coded it.", "exec-2")]);
    const asked = stubProposals((workItemId) =>
      workItemId === "wi-1"
        ? [
            proposal("Proposed at step two", "exec-2", "2026-09-26T10:30:00Z"),
            proposal("Proposed at step one", "exec-1", "2026-09-26T10:05:00Z"),
          ]
        : [],
    );

    render(dialog(makeWorkItem({ latestLoopRunId: "run-1" })), { wrapper: WorkItemHubProvider });

    await waitFor(() => expect(action().textContent).toContain("Proposed at step two"));
    await waitFor(() => expect(action().textContent).toContain("Coded it."));
    expect(asked).toContain("wi-1");
    const expected = ["Planned it.", "Proposed at step one", "Coded it.", "Proposed at step two"];
    expect(documentOrder(expected, action())).toEqual(expected);
    expect(action().textContent).toContain("Release Loop");
    // A finished item opens on Overview; its proposals are decided on the Action tab.
    fireEvent.click(screen.getByRole("tab", { name: /^Action/ }));
    expect(within(action()).getAllByRole("button", { name: "Save" })).toHaveLength(2);
  });

  test("a hint that loop template proposals changed brings in a new card without a reload", async () => {
    const hint = mockServices();
    let served: unknown[] = [];
    stubProposals(() => served);
    render(dialog(makeWorkItem()), { wrapper: WorkItemHubProvider });
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    expect(action().textContent).not.toContain("Made just now");

    served = [proposal("Made just now", null, "2026-09-26T10:00:00Z")];
    await hint("LoopTemplateProposalsChanged", { templateId: "tpl-1" });

    await waitFor(() => expect(action().textContent).toContain("Made just now"));
  });
});
