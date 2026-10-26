import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, cleanup, act, waitFor, within, fireEvent, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import WorkItemModalV2 from "./WorkItemModalV2";
import { LoopRun, LoopRunStatus, WorkItem, WorkItemStatus } from "../../types";
import type { HumanInputProposal } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";
import { fakeHub, reply, stubFetch } from "../../test-support";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  localStorage.clear();
});

const MB = 1024 * 1024;
const LIST = "/api/v1/human-input-proposals";

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
    latestLoopRunId: "run-1",
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

/** A suggestion for run-1's question, told apart by its rationale. */
function suggestion(
  rationale: string,
  overrides: Partial<HumanInputProposal> = {},
): HumanInputProposal {
  return {
    id: `hp-${rationale}`,
    workItemId: "wi-1",
    runId: "run-1",
    runNodeId: "exec-1",
    output: "OnSuccess",
    text: "Ship it.",
    rationale,
    status: "Pending",
    rejectionReason: null,
    decidedBy: null,
    createdAt: "2026-10-01T10:00:00Z",
    decidedAt: null,
    createdByLoopRunId: "run-9",
    createdByChatSessionId: null,
    ...overrides,
  };
}

/**
 * Answers the suggestion listing with whatever `served` holds, and any decision
 * with `decide`; everything else the dialog fetches past the mocked services fails.
 */
function stubSuggestions(
  served: { list: HumanInputProposal[] },
  decide: (path: string) => Response = () => reply(200, {}),
) {
  const calls = stubFetch((call) => {
    if (call.method === "GET" && call.path === LIST) return reply(200, served.list);
    if (call.method === "POST" && call.path.startsWith(`${LIST}/`)) return decide(call.path);
    return reply(404, { error: "not stubbed" });
  });
  const reads = () => calls.filter((c) => c.method === "GET" && c.path === LIST);
  return { calls, reads };
}

function mockServices() {
  const hub = fakeHub();
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue(hub.connection);
  vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([makeRun()]);
  vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getTurnVariables").mockResolvedValue([]);
  vi.spyOn(authServices.loopRunService, "getById").mockResolvedValue(makeRun());
  vi.spyOn(authServices.loopRunService, "getConversation").mockImplementation(
    async (runId: string) => ({ runId, messages: [], lastEventId: null }),
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

const action = () => document.getElementById("wiv2-panel-action") as HTMLElement;

async function openAction() {
  render(
    <MemoryRouter>
      <WorkItemModalV2 workItem={makeWorkItem()} onClose={vi.fn()} onSave={vi.fn()} />
    </MemoryRouter>,
    { wrapper: WorkItemHubProvider },
  );
  fireEvent.click(screen.getByRole("tab", { name: /^Action/ }));
}

const cardOf = (rationale: string) =>
  within(action()).getByText(rationale).closest(".proposal-card") as HTMLElement;

describe("human input suggestions in the Action tab", () => {
  test("shows the suggestions for the displayed run, whichever source made them, and nothing says no action is required", async () => {
    mockServices();
    const { reads } = stubSuggestions({
      list: [
        suggestion("From a loop run"),
        suggestion("From a chat", {
          output: "Rework",
          text: "",
          createdByLoopRunId: null,
          createdByChatSessionId: "chat-1",
        }),
        suggestion("About an earlier run", { runId: "run-0" }),
      ],
    });

    await openAction();

    await waitFor(() => expect(action().textContent).toContain("From a loop run"));
    expect(reads()[0].query.get("workItemId")).toBe("wi-1");
    expect(action().textContent).toContain("From a chat");
    expect(action().textContent).not.toContain("About an earlier run");
    expect(cardOf("From a chat").textContent).toContain("No comment");
    expect(cardOf("From a chat").textContent).toContain("Rework");
    expect(within(action()).queryByText("No action required.")).toBeNull();
  });

  test("approving re-reads the list, and the card then shows it decided with nothing left to press", async () => {
    mockServices();
    const served = { list: [suggestion("Green checks")] };
    const { calls, reads } = stubSuggestions(served, () => {
      served.list = [suggestion("Green checks", { status: "Approved", decidedBy: "admin" })];
      return reply(200, { proposal: served.list[0] });
    });
    await openAction();
    await waitFor(() => expect(action().textContent).toContain("Green checks"));
    const before = reads().length;

    fireEvent.click(within(cardOf("Green checks")).getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(cardOf("Green checks").textContent).toContain("Approved"));
    expect(calls.filter((c) => c.method === "POST").map((c) => c.path)).toEqual([
      `${LIST}/hp-${encodeURIComponent("Green checks")}/approve`,
    ]);
    expect(reads().length).toBeGreaterThan(before);
    expect(within(cardOf("Green checks")).queryByRole("button", { name: "Approve" })).toBeNull();
    expect(within(cardOf("Green checks")).queryByRole("button", { name: "Reject" })).toBeNull();
  });

  test("an approve refused because the question moved on shows the card's new status, not an error", async () => {
    mockServices();
    const served = { list: [suggestion("Green checks")] };
    stubSuggestions(served, () => {
      served.list = [suggestion("Green checks", { status: "Stale" })];
      return reply(409, { error: "The question is no longer open.", proposal: served.list[0] });
    });
    await openAction();
    await waitFor(() => expect(action().textContent).toContain("Green checks"));

    fireEvent.click(within(cardOf("Green checks")).getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(cardOf("Green checks").textContent).toContain("Stale"));
    expect(action().textContent).not.toContain("The request failed");
    expect(action().textContent).not.toContain("no longer open");
  });

  test("a hint for this work item re-reads the list and brings in a new card; a hint for another does not", async () => {
    const hint = mockServices();
    const served = { list: [] as HumanInputProposal[] };
    const { reads } = stubSuggestions(served);
    await openAction();
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    const before = reads().length;

    await hint("HumanInputProposalsChanged", { workItemId: "wi-other" });
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    expect(reads()).toHaveLength(before);

    served.list = [suggestion("Made just now")];
    await hint("HumanInputProposalsChanged", { workItemId: "wi-1" });

    await waitFor(() => expect(action().textContent).toContain("Made just now"));
    expect(reads().length).toBeGreaterThan(before);
  });
});
