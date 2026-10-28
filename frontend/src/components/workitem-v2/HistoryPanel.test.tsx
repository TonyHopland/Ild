import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, cleanup, act, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import HistoryPanel from "./HistoryPanel";
import { WorkItem, WorkItemStatus } from "../../types";
import type { WorkItemHistoryEntry } from "../../types";
import * as authServices from "../../services/auth";
import { repositoryName } from "../../utils/repositoryName";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const HOUR = 60 * 60 * 1000;

function makeWorkItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Test Work Item",
    description: "",
    status: WorkItemStatus.Backlog,
    priority: 0,
    tags: [],
    repositoryId: "repo-1",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: null,
    humanFeedbackActions: null,
    createdAt: "2025-01-01T00:00:00Z",
    startedAt: null,
    completedAt: null,
    currentLoopRunId: null,
    latestLoopRunId: null,
    dependencyIds: [],
    dependentIds: [],
    ...overrides,
  };
}

function entry(overrides: Partial<WorkItemHistoryEntry>): WorkItemHistoryEntry {
  return {
    saveId: "save-1",
    timestamp: new Date(Date.now() - 3 * HOUR).toISOString(),
    kind: "Changed",
    username: "bob",
    actorKind: null,
    actorDetail: null,
    actorWorkItemId: null,
    field: "title",
    oldValue: null,
    newValue: null,
    reason: null,
    ...overrides,
  } as WorkItemHistoryEntry;
}

function mockHistory(history: () => Promise<WorkItemHistoryEntry[]>) {
  return vi.spyOn(authServices.workItemService, "getHistory").mockImplementation(history);
}

const REPOSITORIES = [
  {
    id: "repo-1",
    name: "my-repo",
    remoteProviderId: "rp-1",
    cloneUrl: "",
    defaultBranch: null,
    worktreesPath: null,
    defaultIntakeStatus: WorkItemStatus.Backlog,
    createdAt: "2025-01-01T00:00:00Z",
  },
];

/** A repository list that has loaded, or one that has not. */
const namesFrom = (loaded: boolean) => (id: string) => repositoryName(id, REPOSITORIES, loaded);

function panelFor(
  workItem: WorkItem,
  repositories: (id: string) => string | null = namesFrom(true),
) {
  return (
    <MemoryRouter>
      <HistoryPanel workItem={workItem} active repositoryName={repositories} />
    </MemoryRouter>
  );
}

async function renderPanel(workItem: WorkItem, repositories?: (id: string) => string | null) {
  let result!: ReturnType<typeof render>;
  await act(async () => {
    result = render(panelFor(workItem, repositories));
    await Promise.resolve();
  });
  return { ...result, panel: result.container };
}

const text = (el: Element) => (el.textContent ?? "").replace(/\s+/g, " ").trim();

/** The innermost elements whose own text is exactly the given line. */
function elementsReading(panel: HTMLElement, pattern: RegExp): Element[] {
  return Array.from(panel.querySelectorAll("*")).filter(
    (el) =>
      pattern.test(text(el)) && !Array.from(el.children).some((child) => pattern.test(text(child))),
  );
}

function marks(el: Element) {
  return [el.className, el.getAttribute("title") ?? "", el.getAttribute("aria-label") ?? ""].join(
    " ",
  );
}

describe("HistoryPanel", () => {
  test("a repository change is shown by name, and an id that matches no repository as Unknown repository", async () => {
    mockHistory(async () => [
      entry({
        saveId: "save-1",
        username: "bob",
        field: "repository",
        oldValue: "repo-gone",
        newValue: "repo-1",
      }),
    ]);
    const { panel } = await renderPanel(makeWorkItem());
    await waitFor(() => expect(panel.textContent).toContain("my-repo"));
    expect(panel.textContent).toContain("Unknown repository");
    expect(panel.textContent).not.toContain("repo-gone");
  });

  test("while the repositories are still loading, a repository change shows … for each side, never none", async () => {
    mockHistory(async () => [
      entry({
        saveId: "save-1",
        username: "bob",
        field: "repository",
        oldValue: "repo-a",
        newValue: "repo-b",
      }),
    ]);
    const { panel } = await renderPanel(makeWorkItem(), namesFrom(false));
    await waitFor(() => expect(panel.textContent).toContain("bob"));
    expect(panel.textContent).not.toContain("repo-a");
    expect(panel.textContent).not.toContain("repo-b");
    expect(panel.textContent).not.toContain("none");
    expect(elementsReading(panel, /^…$/)).toHaveLength(2);
  });

  test("lists one group per save, newest first, with the actor, a relative time and each change", async () => {
    const getHistory = mockHistory(async () => [
      entry({
        saveId: "save-3",
        username: "bob",
        actorKind: "Human",
        field: "title",
        oldValue: "Old title",
        newValue: "New title",
      }),
      entry({
        saveId: "save-3",
        username: "bob",
        actorKind: "Human",
        field: "description",
        oldValue: "Line one\nLine two",
        newValue: "Line one\nLine 2",
      }),
      entry({
        saveId: "save-2",
        timestamp: new Date(Date.now() - 30 * HOUR).toISOString(),
        username: "instance-user",
        actorKind: "Agent",
        actorDetail: "AI Coder (run 3f2a)",
        actorWorkItemId: "42",
        field: "status",
        oldValue: "Ready",
        newValue: "Running",
        reason: "Picked up by the scheduler",
      }),
      entry({
        saveId: "save-1",
        timestamp: new Date(Date.now() - 50 * HOUR).toISOString(),
        username: "carol",
        field: "tags",
        oldValue: '["feature","ui"]',
        newValue: '["ui","backend"]',
      }),
    ]);
    const { panel } = await renderPanel(makeWorkItem());
    await waitFor(() => expect(panel.textContent).toContain("carol"));

    expect(getHistory).toHaveBeenCalledWith("wi-1");
    const all = text(panel);
    expect(all.indexOf("bob")).toBeLessThan(all.indexOf("instance-user"));
    expect(all.indexOf("instance-user")).toBeLessThan(all.indexOf("carol"));
    // One save reads as one event: its actor is named once, not once per field.
    expect(all.match(/\bbob\b/g)).toHaveLength(1);
    expect(all).toMatch(/3\s*h(ours?)?\s*ago/i);

    expect(all).toContain("Old title");
    expect(all).toContain("New title");

    // The description is a unified diff: the unchanged line once, the changed
    // one as a removed and an added line — never the two texts in full.
    expect(elementsReading(panel, /^Line one$/)).toHaveLength(1);
    expect(elementsReading(panel, /^[-−]\s*Line two$/)).toHaveLength(1);
    expect(elementsReading(panel, /^\+\s*Line 2$/)).toHaveLength(1);

    expect(all).toContain("Ready");
    expect(all).toContain("Running");
    expect(all).toContain("Picked up by the scheduler");
    expect(all).toContain("AI Coder (run 3f2a)");
    expect(all).toContain("Agent");
    expect(panel.querySelector('a[href="/taskboard/42"]')).not.toBeNull();

    const added = elementsReading(panel, /^\+?\s*backend$/);
    const removed = elementsReading(panel, /^[-−]?\s*feature$/);
    expect(added).toHaveLength(1);
    expect(removed).toHaveLength(1);
    expect(/add/i.test(marks(added[0])) || text(added[0]).startsWith("+")).toBe(true);
    expect(/remov|\bdel/i.test(marks(removed[0])) || /^[-−]/.test(text(removed[0]))).toBe(true);
    expect(all).not.toContain('["');

    // A record of changes, not a conversation: no chat bubbles, no proposal cards.
    expect(panel.querySelector('[class*="bubble"]')).toBeNull();
    expect(
      panel.querySelector('[class*="proposal-card"], [data-testid="proposal-card"]'),
    ).toBeNull();
  });

  test("a baseline is the state at history start and shows nothing for attribution it does not have", async () => {
    mockHistory(async () => [
      entry({
        saveId: "baseline-wi-1",
        kind: "Baseline",
        username: null,
        field: "title",
        oldValue: null,
        newValue: "Title when history began",
      }),
      entry({
        saveId: "baseline-wi-1",
        kind: "Baseline",
        username: null,
        field: "priority",
        oldValue: null,
        newValue: "High",
      }),
    ]);
    const { panel } = await renderPanel(makeWorkItem());
    await waitFor(() => expect(panel.textContent).toContain("Title when history began"));

    const all = text(panel);
    expect(all).toMatch(/State at history start/i);
    expect(all).toContain("High");
    expect(all).not.toMatch(/\b(null|undefined|unknown)\b/i);
    expect(panel.querySelector('a[href^="/taskboard/"]')).toBeNull();
  });

  test("priority changes recorded before the priority became the board order stay readable", async () => {
    mockHistory(async () => [
      entry({
        saveId: "save-2",
        username: "carol",
        field: "priority",
        oldValue: "Medium",
        newValue: "Critical",
      }),
      entry({
        saveId: "save-1",
        kind: "Baseline",
        username: null,
        field: "priority",
        oldValue: null,
        newValue: "Medium",
      }),
    ]);
    const { panel } = await renderPanel(makeWorkItem());
    await waitFor(() => expect(panel.textContent).toContain("carol"));

    const all = text(panel);
    expect(all).toMatch(/changed priority/i);
    expect(all).toContain("Critical");
    expect(all).toMatch(/State at history start/i);
  });

  test("a created save is labelled as the item's creation", async () => {
    mockHistory(async () => [
      entry({
        saveId: "save-1",
        kind: "Created",
        username: "alice",
        actorKind: "Human",
        field: "title",
        newValue: "Brand new item",
      }),
    ]);
    const { panel } = await renderPanel(makeWorkItem());
    await waitFor(() => expect(panel.textContent).toContain("Brand new item"));

    expect(text(panel)).toMatch(/creat/i);
    expect(text(panel)).not.toMatch(/State at history start/i);
  });

  test("loading, empty and failed reads are each visible", async () => {
    mockHistory(() => new Promise<WorkItemHistoryEntry[]>(() => {}));
    let view = await renderPanel(makeWorkItem());
    let panel = view.panel;
    expect(
      /loading/i.test(text(panel)) ||
        panel.querySelector('[aria-busy="true"], [role="status"], [role="progressbar"]') !== null,
    ).toBe(true);
    view.unmount();
    vi.restoreAllMocks();

    mockHistory(async () => []);
    view = await renderPanel(makeWorkItem());
    panel = view.panel;
    await waitFor(() => expect(text(panel)).toMatch(/no (history|changes|entries)|nothing/i));
    view.unmount();
    vi.restoreAllMocks();

    mockHistory(async () => {
      throw new Error("WorkItemServer unreachable");
    });
    ({ panel } = await renderPanel(makeWorkItem()));
    await waitFor(() =>
      expect(
        /error|fail|couldn.t|could not|unable|unreachable/i.test(text(panel)) ||
          panel.querySelector('[role="alert"]') !== null,
      ).toBe(true),
    );
  });

  test("an edit to the item re-reads its history and a reply that arrives late is dropped", async () => {
    const replies: Array<(entries: WorkItemHistoryEntry[]) => void> = [];
    const getHistory = mockHistory(
      () => new Promise<WorkItemHistoryEntry[]>((resolve) => replies.push(resolve)),
    );
    const view = await renderPanel(makeWorkItem({ updatedAt: "2025-01-01T00:00:00Z" }));
    const panel = view.panel;
    expect(getHistory).toHaveBeenCalledTimes(1);

    await act(async () => {
      view.rerender(panelFor(makeWorkItem({ updatedAt: "2025-01-02T00:00:00Z" })));
      await Promise.resolve();
    });
    await waitFor(() => expect(getHistory).toHaveBeenCalledTimes(2));

    await act(async () => {
      replies[1]([entry({ saveId: "save-2", newValue: "After the edit" })]);
      await Promise.resolve();
    });
    await act(async () => {
      replies[0]([entry({ saveId: "save-1", newValue: "Before the edit" })]);
      await Promise.resolve();
    });

    await waitFor(() => expect(panel.textContent).toContain("After the edit"));
    expect(panel.textContent).not.toContain("Before the edit");
  });
  test("a deletion and a linked pull request each read as what happened", async () => {
    mockHistory(async () => [
      entry({
        saveId: "save-2",
        kind: "Deleted",
        username: "alice",
        actorKind: "Human",
        field: "deleted",
        newValue: "The item as it was named",
      }),
      entry({
        saveId: "save-1",
        timestamp: new Date(Date.now() - 5 * HOUR).toISOString(),
        username: "instance-user",
        field: "pullRequest.linked",
        newValue: "https://forge.example/acme/widgets/pulls/7",
      }),
    ]);
    const { panel } = await renderPanel(makeWorkItem());
    await waitFor(() => expect(panel.textContent).toContain("deleted the item"));

    const all = text(panel);
    expect(all).toContain("The item as it was named");
    expect(all).toContain("linked a pull request");
    expect(
      panel.querySelector('a[href="https://forge.example/acme/widgets/pulls/7"]'),
    ).not.toBeNull();
    expect(all).not.toMatch(/pullRequest|\bnull\b/);
  });
});
