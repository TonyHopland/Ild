import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import Taskboard from "./index";
import { WorkItem, WorkItemStatus } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";
import {
  fakeHub,
  makeGlobalVariableProposal,
  mockTaskboardServer,
  reply,
  stubFetch,
} from "../../test-support";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";

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

function suggestion(id: string, rationale: string) {
  return makeGlobalVariableProposal({
    id,
    value: `Suggested text for ${id}`,
    rationale,
    createdAt: "2026-09-26T10:00:00Z",
    snapshotUpdatedAt: "2026-09-01T00:00:00Z",
    currentValue: "Keep reviews short.",
    createdByLoopRunId: "run-old",
    createdByRunNodeId: null,
  });
}

function mockDialogServices(item: WorkItem) {
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue(fakeHub().connection);
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
  stubFetch((call) =>
    call.path === "/api/v1/global-variables/proposals" && call.query.get("workItemId") === item.id
      ? reply(200, [
          suggestion("gp-other", "Another suggestion"),
          suggestion("gp-7", "The linked suggestion"),
        ])
      : reply(404, { error: "not stubbed" }),
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
        { wrapper: WorkItemHubProvider },
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
