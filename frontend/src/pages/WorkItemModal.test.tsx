import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import WorkItemModal from "../components/WorkItemModal";
import { WorkItemStatus, WorkItemPriority, WorkItem, LoopRun, LoopRunStatus } from "../types";

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

function renderModal(
  mockFetchFn: ReturnType<typeof mockFetch>,
  props?: { isOpen?: boolean; workItem?: null },
) {
  vi.stubGlobal("fetch", mockFetchFn);

  render(
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
    labels: [],
    loopTemplateId: "tmpl-1",
    loopTemplateVersion: "v1",
    repositoryId: "repo-1",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: null,
    createdAt: "2025-01-01T00:00:00Z",
    startedAt: null,
    completedAt: null,
    dependencyIds: [],
    dependentIds: [],
    ...overrides,
  };
}

describe("WorkItemModal", () => {
  test("create form shows repository and loop template dropdowns", async () => {
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

    renderModal(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("New Work Item")).toBeTruthy();
    });

    expect(screen.getByLabelText("Repository")).toBeTruthy();
    expect(screen.getByLabelText("Loop Template")).toBeTruthy();
    expect(screen.getByText("my-repo")).toBeTruthy();
    expect(screen.getByText("Feature Dev")).toBeTruthy();
  });

  test("detail view shows Start button when WorkItem status is Ready", async () => {
    const workItem = makeWorkItem({ status: WorkItemStatus.Ready });

    const fetchMock = mockFetch([]);

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    render(<WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />);

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

    render(
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

    render(
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

    render(
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

    render(
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

    render(<WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />);

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

    render(<WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />);

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

    render(<WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />);

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

    render(<WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />);

    expect(screen.queryByText("Mark Merged")).toBeFalsy();
  });

  test("detail view shows Human Feedback input section when reason is Human Input Needed", async () => {
    const workItem = makeWorkItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "Human Input Needed",
    });

    const fetchMock = mockFetch([]);

    vi.stubGlobal("fetch", fetchMock);

    const onClose = vi.fn();
    const onSave = vi.fn();

    render(<WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />);

    await waitFor(() => {
      expect(screen.getByText("Human Feedback")).toBeTruthy();
    });

    expect(screen.getByText("Continue")).toBeTruthy();
    expect(screen.getByText("Reject")).toBeTruthy();
  });

  test("clicking Continue calls the human-feedback/input API endpoint", async () => {
    const workItem = makeWorkItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "Human Input Needed",
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

    render(<WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />);

    await waitFor(() => {
      expect(screen.getByText("Continue")).toBeTruthy();
    });

    const textarea = document.querySelector("textarea") as HTMLTextAreaElement;
    if (textarea) {
      fireEvent.change(textarea, { target: { value: "proceed with the change" } });
    }

    fireEvent.click(screen.getByText("Continue"));

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

    render(<WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />);

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

    render(<WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />);

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

    render(<WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />);

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

    render(<WorkItemModal workItem={workItem} isOpen={true} onClose={onClose} onSave={onSave} />);

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
});
