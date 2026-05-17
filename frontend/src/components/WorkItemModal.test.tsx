import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup, act } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import WorkItemModal from "./WorkItemModal";
import { WorkItemStatus, WorkItemPriority, WorkItem, LoopRun, LoopRunStatus } from "../types";
import * as signalRHook from "../hooks/useSignalR";
import * as authServices from "../services/auth";

afterEach(() => {
  cleanup();
});

function mockFetch(json: unknown, status = 200) {
  const body = JSON.stringify(json);
  return vi.fn().mockResolvedValue({
    ok: status < 400,
    status,
    text: () => Promise.resolve(body),
    json: () => Promise.resolve(JSON.parse(body)),
  });
}

async function renderWithEffectsSettled(ui: React.ReactElement) {
  let result: ReturnType<typeof render> | undefined;
  await act(async () => {
    result = render(ui);
    await Promise.resolve();
  });
  return result!;
}

async function dispatchSignalR(handler: (msg: any) => void, payload: unknown) {
  await act(async () => {
    handler({ payload });
  });
}

async function renderModal(
  mockFetchFn: ReturnType<typeof mockFetch>,
  props?: { isOpen?: boolean; workItem?: null },
) {
  vi.stubGlobal("fetch", mockFetchFn);

  await renderWithEffectsSettled(
    <WorkItemModal
      workItem={props?.workItem ?? null}
      isOpen={props?.isOpen ?? true}
      onClose={vi.fn()}
      onSave={vi.fn()}
    />,
  );
}

function makeWorkItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Test Work Item",
    description: "A test item",
    status: WorkItemStatus.Ready,
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
    ...overrides,
  };
}

describe("WorkItemModal", () => {
  test("create form shows repository dropdown and tag autocomplete from template names", async () => {
    const repos = [
      {
        id: "repo-1",
        name: "my-repo",
        cloneUrl: "https://git.example.com/my-repo.git",
        remoteProviderId: "prov-1",
        defaultBranch: "main",
        worktreesPath: null,
        defaultIntakeStatus: "Backlog",
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const templates = [
      {
        id: "tmpl-1",
        name: "Feature Dev",
        description: "Standard feature workflow",
        version: 1,
        nodes: [],
        edges: [],
        createdAt: "2025-01-01T00:00:00Z",
        updatedAt: "2025-01-01T00:00:00Z",
      },
    ];

    const fetchMock = mockFetch(null);
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(repos)),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(templates)),
        }),
      );

    await renderModal(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("New Work Item")).toBeTruthy();
    });

    expect(screen.getByLabelText("Repository")).toBeTruthy();
    // Tag input is now the user-facing way to pick a loop template; it
    // renders a custom suggestion list (per-segment autocomplete) sourced
    // from loop template names.
    const tagInput = screen.getByLabelText(/Tags/i) as HTMLInputElement;
    expect(tagInput).toBeTruthy();
    expect(screen.getByText("my-repo")).toBeTruthy();
  });

  test("detail view shows Start button when WorkItem status is Ready", async () => {
    const workItem = makeWorkItem({ status: WorkItemStatus.Ready });

    const fetchMock = mockFetch([]);

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />,
    );

    expect(screen.getByText(workItem.title)).toBeTruthy();
    expect(screen.getByText("Ready")).toBeTruthy();
    expect(screen.getByText("Start")).toBeTruthy();
  });

  test("clicking Start calls the start API endpoint", async () => {
    const workItem = makeWorkItem({ status: WorkItemStatus.Ready });
    const startedWorkItem = makeWorkItem({
      status: WorkItemStatus.Running,
      startedAt: "2025-03-01T00:00:00Z",
    });

    const fetchMock = mockFetch(null);
    // Initial fetches for repositories, templates, runs, dependencies, allWorkItems
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      // Start API call
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 202,
          text: () => Promise.resolve(JSON.stringify({})),
          json: () => Promise.resolve({}),
        }),
      )
      // Get updated work item
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(startedWorkItem)),
          json: () => Promise.resolve(startedWorkItem),
        }),
      );

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <MemoryRouter>
        <WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("Start")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("Start"));

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith(startedWorkItem);
    });

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining("/workitems/wi-1/start"),
      expect.objectContaining({ method: "POST" }),
    );
  });

  test("detail view shows dependency list with clickable IDs", async () => {
    const workItem = makeWorkItem({
      status: WorkItemStatus.Ready,
      dependencyIds: ["dep-1", "dep-2"],
    });

    const dep1 = makeWorkItem({ id: "dep-1", title: "dep-1" });
    const dep2 = makeWorkItem({ id: "dep-2", title: "dep-2" });

    const fetchMock = mockFetch(null);
    // repos, templates, runs
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      // getDependencies
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([dep1, dep2])),
          json: () => Promise.resolve([dep1, dep2]),
        }),
      )
      // getAll
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      );

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <MemoryRouter>
        <WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />
      </MemoryRouter>,
    );

    expect(screen.getByText("Dependencies")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText("dep-1")).toBeTruthy();
      expect(screen.getByText("dep-2")).toBeTruthy();
    });
  });

  test("detail view shows loop run history with status and timing", async () => {
    const workItem = makeWorkItem({
      status: WorkItemStatus.Running,
    });

    const runs: LoopRun[] = [
      {
        id: "run-1",
        workItemId: "wi-1",
        loopTemplateId: "tmpl-1",
        templateVersion: 1,
        status: LoopRunStatus.Completed,
        currentNodeId: null,
        isPaused: false,
        nodeExecutionCount: 5,
        startedAt: "2025-02-01T10:00:00Z",
        completedAt: "2025-02-01T10:30:00Z",
        nodes: [],
      },
      {
        id: "run-2",
        workItemId: "wi-1",
        loopTemplateId: "tmpl-1",
        templateVersion: 1,
        status: LoopRunStatus.Failed,
        currentNodeId: "node-3",
        isPaused: false,
        nodeExecutionCount: 3,
        startedAt: "2025-03-01T14:00:00Z",
        completedAt: "2025-03-01T14:15:00Z",
        nodes: [],
      },
    ];

    const fetchMock = mockFetch([]);
    // Initial fetches for repos and templates, then runs
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(runs)),
          json: () => Promise.resolve(runs),
        }),
      );

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <MemoryRouter>
        <WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("Run History")).toBeTruthy();
    });

    expect(screen.getByText("Completed")).toBeTruthy();
    expect(screen.getByText("Failed")).toBeTruthy();
  });

  test("clicking a run in history navigates to loop run monitor", async () => {
    const workItem = makeWorkItem({
      status: WorkItemStatus.Running,
    });

    const runs: LoopRun[] = [
      {
        id: "run-1",
        workItemId: "wi-1",
        loopTemplateId: "tmpl-1",
        templateVersion: 1,
        status: LoopRunStatus.Completed,
        currentNodeId: null,
        isPaused: false,
        nodeExecutionCount: 5,
        startedAt: "2025-02-01T10:00:00Z",
        completedAt: "2025-02-01T10:30:00Z",
        nodes: [],
      },
    ];

    const fetchMock = mockFetch([]);
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(runs)),
          json: () => Promise.resolve(runs),
        }),
      );

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <MemoryRouter>
        <WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("Completed")).toBeTruthy();
    });

    const runItems = screen.getAllByText("Completed");
    fireEvent.click(runItems[0]);

    await waitFor(() => {
      const links = screen.getAllByRole("link");
      expect(links.some((l) => l.getAttribute("href") === "/loop-runs/run-1")).toBeTruthy();
    });
  });

  test("detail view shows PR URL as clickable link when set", async () => {
    const prUrl = "https://forgejo.example.com/repo/pull/42";
    const workItem = makeWorkItem({
      status: WorkItemStatus.HumanFeedback,
      prUrl: prUrl,
    });

    const fetchMock = mockFetch([]);

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />,
    );

    expect(screen.getByText("Pull Request")).toBeTruthy();
    const prLink = screen.getByText(prUrl);
    expect(prLink).toBeTruthy();
    expect(prLink.getAttribute("href")).toBe(prUrl);
  });

  test("detail view shows Link PR button and input form", async () => {
    const workItem = makeWorkItem({
      status: WorkItemStatus.Running,
      prUrl: null,
    });

    const fetchMock = mockFetch([]);

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />,
    );

    expect(screen.getByText("Pull Request")).toBeTruthy();
    expect(screen.getByText("No PR linked")).toBeTruthy();
    expect(screen.getByText("Link PR")).toBeTruthy();

    fireEvent.click(screen.getByText("Link PR"));

    await waitFor(() => {
      expect(screen.getByPlaceholderText("https://forgejo/pr/...")).toBeTruthy();
    });
  });

  test("detail view shows Mark Merged button only when PR URL is set", async () => {
    const workItem = makeWorkItem({
      status: WorkItemStatus.HumanFeedback,
      prUrl: "https://forgejo.example.com/repo/pull/42",
    });

    const fetchMock = mockFetch([]);

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />,
    );

    expect(screen.getByText("Mark Merged")).toBeTruthy();
  });

  test("detail view hides Mark Merged button when no PR URL", async () => {
    const workItem = makeWorkItem({
      status: WorkItemStatus.Running,
      prUrl: null,
    });

    const fetchMock = mockFetch([]);

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />,
    );

    expect(screen.queryByText("Mark Merged")).toBeFalsy();
  });

  test("detail view shows Human Feedback input section when reason is Human Input Needed", async () => {
    const workItem = makeWorkItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "Human Input Needed",
      humanFeedbackActions: "OnSuccess,OnRespond,OnFailure",
    });

    const fetchMock = mockFetch([]);

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />,
    );

    await waitFor(() => {
      expect(screen.getByText("Human Feedback")).toBeTruthy();
    });

    expect(screen.getByText("Approve")).toBeTruthy();
    expect(screen.getByText("Respond")).toBeTruthy();
    expect(screen.getByText("Reject")).toBeTruthy();
  });

  test("clicking Approve calls the human-feedback/input API endpoint", async () => {
    const workItem = makeWorkItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "Human Input Needed",
      humanFeedbackActions: "OnSuccess,OnFailure",
    });

    const fetchMock = mockFetch([]);
    // Initial fetches for repos, templates, runs
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      );

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />,
    );

    await waitFor(() => {
      expect(screen.getByText("Approve")).toBeTruthy();
    });

    const textarea = document.querySelector("textarea") as HTMLTextAreaElement;
    if (textarea) {
      fireEvent.change(textarea, { target: { value: "proceed with the change" } });
    }

    fireEvent.click(screen.getByText("Approve"));

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining("/workitems/wi-1/human-feedback/input"),
        expect.objectContaining({ method: "POST" }),
      );
    });
  });

  test("clicking Reject calls the human-feedback/reject API endpoint", async () => {
    const workItem = makeWorkItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "Human Input Needed",
      humanFeedbackActions: "OnSuccess,OnFailure",
    });

    const fetchMock = mockFetch([]);
    // Initial fetches for repos, templates, runs
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      );

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />,
    );

    await waitFor(() => {
      expect(screen.getByText("Reject")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("Reject"));

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining("/workitems/wi-1/human-feedback/reject"),
        expect.objectContaining({ method: "POST" }),
      );
    });
  });

  test("detail view shows cleanup buttons for failed WorkItem", async () => {
    const workItem = makeWorkItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "Node Failed",
    });

    const fetchMock = mockFetch([]);

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />,
    );

    await waitFor(() => {
      expect(screen.getByText("Human Feedback")).toBeTruthy();
    });

    expect(screen.getByText("Cleanup -> Done")).toBeTruthy();
    expect(screen.getByText("Cleanup -> Backlog")).toBeTruthy();
  });

  test("clicking Cleanup Done calls the cleanup-to-done API endpoint", async () => {
    const workItem = makeWorkItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "Node Failed",
    });

    const fetchMock = mockFetch([]);
    // Initial fetches for repos, templates, runs
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      );

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />,
    );

    await waitFor(() => {
      expect(screen.getByText("Cleanup -> Done")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("Cleanup -> Done"));

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining("/workitems/wi-1/cleanup-to-done"),
        expect.objectContaining({ method: "POST" }),
      );
    });
  });

  test("clicking Cleanup Backlog calls the cleanup-to-backlog API endpoint", async () => {
    const workItem = makeWorkItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "Node Failed",
    });

    const fetchMock = mockFetch([]);
    // Initial fetches for repos, templates, runs
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
          json: () => Promise.resolve([]),
        }),
      );

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />,
    );

    await waitFor(() => {
      expect(screen.getByText("Cleanup -> Backlog")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("Cleanup -> Backlog"));

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining("/workitems/wi-1/cleanup-to-backlog"),
        expect.objectContaining({ method: "POST" }),
      );
    });
  });

  test("shows live output section when work item is Running with a currentLoopRunId", async () => {
    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: vi.fn(),
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

    const workItem = makeWorkItem({
      status: WorkItemStatus.Running,
      currentLoopRunId: "run-active-1",
    });

    const fetchMock = mockFetch([]);

    vi.stubGlobal("fetch", fetchMock);

    await renderWithEffectsSettled(
      <MemoryRouter>
        <WorkItemModal workItem={workItem} isOpen={true} onClose={vi.fn()} onSave={vi.fn()} />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("Live Output")).toBeTruthy();
    });
  });

  test("hides live output section when work item is Running without a currentLoopRunId", async () => {
    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: vi.fn(),
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

    const workItem = makeWorkItem({
      status: WorkItemStatus.Running,
      currentLoopRunId: null,
    });

    const fetchMock = mockFetch([]);

    vi.stubGlobal("fetch", fetchMock);

    await renderWithEffectsSettled(
      <MemoryRouter>
        <WorkItemModal workItem={workItem} isOpen={true} onClose={vi.fn()} onSave={vi.fn()} />
      </MemoryRouter>,
    );

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 200));
    });
    expect(screen.queryByText("Live Output")).toBeFalsy();
  });

  test("auto-refetches work item when LoopRunStateChanged event fires", async () => {
    const runHandlers: Record<string, ((msg: any) => void)[]> = {};
    const mockRunOn = vi.fn((eventType: string, handler: (msg: any) => void) => {
      runHandlers[eventType] = runHandlers[eventType] || [];
      runHandlers[eventType].push(handler);
    });

    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: mockRunOn,
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

    const workItem = makeWorkItem({
      status: WorkItemStatus.Running,
      currentLoopRunId: "run-active-1",
    });

    const completedWorkItem = makeWorkItem({
      status: WorkItemStatus.Done,
      currentLoopRunId: null,
      completedAt: "2025-06-01T00:00:00Z",
    });

    const fetchMock = mockFetch([]);

    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(completedWorkItem);

    vi.stubGlobal("fetch", fetchMock);

    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <MemoryRouter>
        <WorkItemModal workItem={workItem} isOpen={true} onClose={vi.fn()} onSave={onSave} />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("Live Output")).toBeTruthy();
    });

    await dispatchSignalR(runHandlers["LoopRunStateChanged"]![0], {
      runId: "run-active-1",
      oldStatus: "Running",
      newStatus: "Completed",
    });

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith(completedWorkItem);
    });
  });

  test("auto-refetches work item when NodeStateChanged event fires", async () => {
    const runHandlers: Record<string, ((msg: any) => void)[]> = {};
    const mockRunOn = vi.fn((eventType: string, handler: (msg: any) => void) => {
      runHandlers[eventType] = runHandlers[eventType] || [];
      runHandlers[eventType].push(handler);
    });

    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: mockRunOn,
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

    const workItem = makeWorkItem({
      status: WorkItemStatus.Running,
      currentLoopRunId: "run-active-1",
    });

    const updatedWorkItem = makeWorkItem({
      status: WorkItemStatus.HumanFeedback,
      currentLoopRunId: "run-active-1",
      humanFeedbackReason: "Human Input Needed",
    });

    const fetchMock = mockFetch([]);

    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(updatedWorkItem);

    vi.stubGlobal("fetch", fetchMock);

    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <MemoryRouter>
        <WorkItemModal workItem={workItem} isOpen={true} onClose={vi.fn()} onSave={onSave} />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("Live Output")).toBeTruthy();
    });

    await dispatchSignalR(runHandlers["NodeStateChanged"]![0], {
      runId: "run-active-1",
      nodeId: "node-1",
      oldStatus: "Running",
      newStatus: "Succeeded",
    });

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith(updatedWorkItem);
    });
  });

  test("hides live output when run completes and work item status changes", async () => {
    const runHandlers: Record<string, ((msg: any) => void)[]> = {};
    const mockRunOn = vi.fn((eventType: string, handler: (msg: any) => void) => {
      runHandlers[eventType] = runHandlers[eventType] || [];
      runHandlers[eventType].push(handler);
    });

    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: mockRunOn,
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

    const runningWorkItem = makeWorkItem({
      status: WorkItemStatus.Running,
      currentLoopRunId: "run-active-1",
    });

    const doneWorkItem = makeWorkItem({
      status: WorkItemStatus.Done,
      currentLoopRunId: null,
      completedAt: "2025-06-01T00:00:00Z",
    });

    const fetchMock = mockFetch([]);

    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(doneWorkItem);

    vi.stubGlobal("fetch", fetchMock);

    let currentWorkItem = runningWorkItem;
    const onSave = vi.fn((wi: WorkItem) => {
      currentWorkItem = wi;
    });

    const { rerender } = await renderWithEffectsSettled(
      <MemoryRouter>
        <WorkItemModal workItem={currentWorkItem} isOpen={true} onClose={vi.fn()} onSave={onSave} />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("Live Output")).toBeTruthy();
    });

    await dispatchSignalR(runHandlers["LoopRunStateChanged"]![0], {
      runId: "run-active-1",
      oldStatus: "Running",
      newStatus: "Completed",
    });

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith(doneWorkItem);
    });

    await act(async () => {
      rerender(
        <MemoryRouter>
          <WorkItemModal workItem={doneWorkItem} isOpen={true} onClose={vi.fn()} onSave={onSave} />
        </MemoryRouter>,
      );
      await Promise.resolve();
    });

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 200));
    });
    expect(screen.queryByText("Live Output")).toBeFalsy();
    expect(screen.getByText("Done")).toBeTruthy();
  });

  test("re-renders conversation when workItem.conversation changes but status stays the same", async () => {
    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: vi.fn(),
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

    const initialWorkItem = makeWorkItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "Human Input Needed",
      humanFeedbackActions: "OnSuccess,OnFailure",
      conversation: [
        { role: "ai", content: "AI initial message", timestamp: "2025-01-01T00:00:00Z" },
      ],
    });

    const updatedWorkItem = makeWorkItem({
      ...initialWorkItem,
      conversation: [
        { role: "ai", content: "AI initial message", timestamp: "2025-01-01T00:00:00Z" },
        { role: "human", content: "Human response", timestamp: "2025-01-01T01:00:00Z" },
        { role: "ai", content: "AI follow-up response", timestamp: "2025-01-01T02:00:00Z" },
      ],
    });

    const fetchMock = mockFetch([]);
    vi.stubGlobal("fetch", fetchMock);

    const onSave = vi.fn();

    const { rerender } = await renderWithEffectsSettled(
      <MemoryRouter>
        <WorkItemModal workItem={initialWorkItem} isOpen={true} onClose={vi.fn()} onSave={onSave} />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("AI initial message")).toBeTruthy();
    });

    // Rerender with updated conversation (same status, same ID)
    await act(async () => {
      rerender(
        <MemoryRouter>
          <WorkItemModal
            workItem={updatedWorkItem}
            isOpen={true}
            onClose={vi.fn()}
            onSave={onSave}
          />
        </MemoryRouter>,
      );
      await Promise.resolve();
    });

    // The new AI message should appear without requiring a page refresh
    await waitFor(() => {
      expect(screen.getByText("AI follow-up response")).toBeTruthy();
    });
    expect(screen.getByText("Human response")).toBeTruthy();
  });

  test("performs delayed refetch after LoopRunStateChanged to catch persisted conversation data", async () => {
    const runHandlers: Record<string, ((msg: any) => void)[]> = {};
    const mockRunOn = vi.fn((eventType: string, handler: (msg: any) => void) => {
      runHandlers[eventType] = runHandlers[eventType] || [];
      runHandlers[eventType].push(handler);
    });

    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: mockRunOn,
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });

    const workItem = makeWorkItem({
      status: WorkItemStatus.Running,
      currentLoopRunId: "run-active-1",
    });

    // First refetch returns stale data (conversation not yet persisted)
    const staleWorkItem = makeWorkItem({
      status: WorkItemStatus.HumanFeedback,
      currentLoopRunId: "run-active-1",
      humanFeedbackReason: "Human Input Needed",
      conversation: [],
    });

    // Delayed refetch returns fresh data with conversation
    const freshWorkItem = makeWorkItem({
      status: WorkItemStatus.HumanFeedback,
      currentLoopRunId: "run-active-1",
      humanFeedbackReason: "Human Input Needed",
      conversation: [
        { role: "ai", content: "AI response message", timestamp: "2025-01-01T00:00:00Z" },
      ],
    });

    const fetchMock = mockFetch([]);
    vi.stubGlobal("fetch", fetchMock);

    let callCount = 0;
    vi.spyOn(authServices.workItemService, "getById").mockImplementation(() => {
      callCount++;
      return callCount === 1 ? Promise.resolve(staleWorkItem) : Promise.resolve(freshWorkItem);
    });

    const onSave = vi.fn();

    await renderWithEffectsSettled(
      <MemoryRouter>
        <WorkItemModal workItem={workItem} isOpen={true} onClose={vi.fn()} onSave={onSave} />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("Live Output")).toBeTruthy();
    });

    // Trigger the SignalR event
    await dispatchSignalR(runHandlers["LoopRunStateChanged"]![0], {
      runId: "run-active-1",
      oldStatus: "Running",
      newStatus: "WaitingHuman",
    });

    // Should refetch at least twice: immediate + delayed
    await waitFor(() => {
      expect(callCount).toBeGreaterThan(1);
    });

    // The final onSave call should have the fresh data with conversation
    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith(freshWorkItem);
    });
  }, 15000);
});
