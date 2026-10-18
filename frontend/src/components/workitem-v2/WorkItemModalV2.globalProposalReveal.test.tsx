import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, cleanup, act, waitFor, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import WorkItemModalV2 from "./WorkItemModalV2";
import {
  LoopRun,
  LoopRunStatus,
  RunConversationMessage,
  WorkItem,
  WorkItemPriority,
  WorkItemStatus,
} from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";
import { fakeHub, makeGlobalVariableProposal } from "../../test-support";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  localStorage.clear();
});

const workItem: WorkItem = {
  id: "wi-1",
  title: "An item",
  description: "",
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
  latestLoopRunId: "run-1",
  dependencyIds: [],
  dependentIds: [],
  attachments: [],
};

const run: LoopRun = {
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

const turn: RunConversationMessage = {
  id: 1,
  runId: "run-1",
  runNodeId: "exec-1",
  role: "ai",
  name: "Coder",
  text: "Planned it.",
  timestamp: "2026-09-26T10:00:00Z",
};

const linked = makeGlobalVariableProposal({
  id: "gp-7",
  value: "Suggested text",
  rationale: "The linked suggestion",
  createdAt: "2026-09-26T10:05:00Z",
  snapshotUpdatedAt: "2026-09-01T00:00:00Z",
  currentValue: "Keep reviews short.",
});

describe("a link to a suggestion in the Action tab", () => {
  test("waits for the run's turns before scrolling to the card, so none lands above it afterwards", async () => {
    vi.spyOn(signalRHook, "useSignalR").mockReturnValue(fakeHub().connection);
    vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
    vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);
    vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);
    vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([run]);
    vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([]);
    vi.spyOn(authServices.workItemService, "getTurnVariables").mockResolvedValue([]);
    vi.spyOn(authServices.loopRunService, "getById").mockResolvedValue(run);
    vi.spyOn(authServices.loopRunService, "getEvents").mockResolvedValue({
      entries: [],
      nextCursor: 0,
    } as unknown as Awaited<ReturnType<typeof authServices.loopRunService.getEvents>>);
    vi.spyOn(authServices.workItemService, "listEditProposals").mockResolvedValue([]);
    vi.spyOn(authServices.workItemService, "listRequestedEditProposals").mockResolvedValue([]);
    vi.spyOn(authServices.settingsService, "getAttachmentLimits").mockResolvedValue({
      maxBytesPerFile: 1,
      maxFilesPerRequest: 1,
      maxTotalBytesPerWorkItem: 1,
    });
    vi.spyOn(authServices.globalVariableService, "listProposals").mockResolvedValue([linked]);
    let answerConversation: () => void = () => {};
    vi.spyOn(authServices.loopRunService, "getConversation").mockImplementation(
      (runId: string, after?: number) =>
        after
          ? Promise.resolve({ runId, messages: [], lastEventId: after })
          : new Promise((resolve) => {
              answerConversation = () => resolve({ runId, messages: [turn], lastEventId: 1 });
            }),
    );
    const scrolled: Element[] = [];
    const original = Object.getOwnPropertyDescriptor(Element.prototype, "scrollIntoView");
    Element.prototype.scrollIntoView = vi.fn(function (this: Element) {
      scrolled.push(this);
    });
    try {
      render(
        <MemoryRouter>
          <WorkItemModalV2
            workItem={workItem}
            onClose={vi.fn()}
            onSave={vi.fn()}
            revealGlobalProposalId="gp-7"
          />
        </MemoryRouter>,
      );
      const rationale = await screen.findByText("The linked suggestion");
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 0));
      });
      expect(scrolled.filter((el) => el.contains(rationale))).toEqual([]);

      await act(async () => answerConversation());

      await waitFor(() =>
        expect(
          scrolled.some((el) => el.contains(rationale) && !el.textContent?.includes("Planned it.")),
        ).toBe(true),
      );
    } finally {
      if (original) Object.defineProperty(Element.prototype, "scrollIntoView", original);
      else delete (Element.prototype as { scrollIntoView?: unknown }).scrollIntoView;
    }
  });
});
