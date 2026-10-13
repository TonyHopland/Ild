import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import Taskboard from "./index";
import { WorkItem, WorkItemStatus, WorkItemPriority } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";
import { mockTaskboardServer } from "../../test-support";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  localStorage.clear();
});

const MB = 1024 * 1024;

function makeItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Backlog item",
    description: "",
    status: WorkItemStatus.Backlog,
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

function suggestion(id: string, rationale: string) {
  return {
    id,
    name: "review_rules",
    value: `Suggested text for ${id}`,
    rationale,
    status: "Pending",
    rejectionReason: null,
    createdAt: "2026-09-26T10:00:00Z",
    decidedAt: null,
    snapshotUpdatedAt: "2026-09-01T00:00:00Z",
    currentValue: "Keep reviews short.",
    variableExists: true,
    createdByLoopRunId: "run-old",
    createdByRunNodeId: null,
    createdByChatSessionId: null,
    chatReplySequence: null,
    source: { kind: "run", workItemId: "wi-1", chatSessionId: null, linkable: true },
  };
}

function mockDialogServices(item: WorkItem) {
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn(() => Promise.resolve()),
    connectionState: "connected",
  } as unknown as ReturnType<typeof signalRHook.useSignalR>);
  mockTaskboardServer([item]);
  vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(item);
  vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getTurnVariables").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "listEditProposals").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "listRequestedEditProposals").mockResolvedValue([]);
  vi.spyOn(authServices.settingsService, "getAttachmentLimits").mockResolvedValue({
    maxBytesPerFile: 25 * MB,
    maxFilesPerRequest: 10,
    maxTotalBytesPerWorkItem: 250 * MB,
  });
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: string) => {
      const url = new URL(input, "http://localhost");
      const listed =
        url.pathname === "/api/v1/global-variables/proposals" &&
        url.searchParams.get("workItemId") === item.id;
      const body = listed
        ? [
            suggestion("gp-other", "Another suggestion"),
            suggestion("gp-7", "The linked suggestion"),
          ]
        : { error: "not stubbed" };
      return {
        ok: listed,
        status: listed ? 200 : 404,
        statusText: "",
        text: () => Promise.resolve(JSON.stringify(body)),
      } as Response;
    }),
  );
}

describe("a link to a suggestion made by a work item's run", () => {
  test("opens the item on its Action tab scrolled to the suggestion's card", async () => {
    mockDialogServices(makeItem());
    const scrolled: Element[] = [];
    const original = Object.getOwnPropertyDescriptor(Element.prototype, "scrollIntoView");
    Element.prototype.scrollIntoView = vi.fn(function (this: Element) {
      scrolled.push(this);
    });
    try {
      render(
        <MemoryRouter initialEntries={["/taskboard/wi-1?globalProposal=gp-7"]}>
          <Routes>
            <Route path="/taskboard" element={<Taskboard />} />
            <Route path="/taskboard/:workItemId" element={<Taskboard />} />
          </Routes>
        </MemoryRouter>,
      );

      await waitFor(() =>
        expect(screen.getByRole("tab", { name: /Action/ }).getAttribute("aria-selected")).toBe(
          "true",
        ),
      );
      const rationale = await screen.findByText("The linked suggestion", { exact: false });
      await waitFor(() =>
        expect(
          scrolled.some(
            (el) =>
              el.contains(rationale) && !(el.textContent ?? "").includes("Another suggestion"),
          ),
        ).toBe(true),
      );
    } finally {
      if (original) Object.defineProperty(Element.prototype, "scrollIntoView", original);
      else delete (Element.prototype as { scrollIntoView?: unknown }).scrollIntoView;
    }
  });
});
