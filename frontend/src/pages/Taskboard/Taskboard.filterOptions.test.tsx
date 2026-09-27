import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, within, cleanup, waitFor, act, fireEvent } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import Taskboard from "./index";
import { mockTaskboardServer } from "../../test-support";
import { WorkItemStatus, WorkItemPriority, WorkItem, Repository } from "../../types";
import * as authServices from "../../services/auth";
import * as signalRHook from "../../hooks/useSignalR";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function makeItem(id: string, tags: string[]): WorkItem {
  return {
    id,
    title: id,
    description: "",
    status: WorkItemStatus.Backlog,
    priority: WorkItemPriority.Medium,
    tags,
    loopTemplateId: "tmpl-1",
    loopTemplateVersion: "v1",
    repositoryId: "repo-1",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: null,
    humanFeedbackActions: null,
    createdAt: "2026-01-01T00:00:00Z",
    startedAt: null,
    completedAt: null,
    currentLoopRunId: null,
    dependencyIds: [],
    dependentIds: [],
  };
}

function makeRepo(n: number): Repository {
  return {
    id: `repo-${n}`,
    name: `Repo ${String(n).padStart(4, "0")}`,
    remoteProviderId: "rp-1",
    cloneUrl: "https://example.com/repo.git",
    defaultBranch: "main",
    worktreesPath: null,
    defaultIntakeStatus: WorkItemStatus.Backlog,
    createdAt: "2025-01-01T00:00:00Z",
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}

function mockBoard(items: WorkItem[], repositories: Repository[] = [makeRepo(1)]) {
  const handlers: Record<string, ((msg: unknown) => void)[]> = {};
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: vi.fn((event: string, handler: (msg: unknown) => void) => {
      (handlers[event] ??= []).push(handler);
    }),
    off: vi.fn(),
    invoke: vi.fn(),
    connectionState: "connected",
  } as unknown as ReturnType<typeof signalRHook.useSignalR>);
  const server = mockTaskboardServer(items);
  vi.spyOn(authServices.workItemService, "getById").mockImplementation(async (id: string) => ({
    ...server.items.find((wi) => wi.id === id)!,
  }));
  const getRepositories = vi
    .spyOn(authServices.repositoryService, "getAll")
    .mockImplementation(async (opts) =>
      repositories.slice(opts?.skip ?? 0, (opts?.skip ?? 0) + (opts?.take ?? 100)),
    );
  vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
    key: "scheduler.isPaused",
    value: "false",
  });
  const emit = async (event: string, payload: unknown) => {
    await act(async () => {
      for (const handler of handlers[event] ?? []) handler({ payload });
    });
  };
  return { server, emit, getRepositories };
}

function renderTaskboard() {
  return render(
    <MemoryRouter initialEntries={["/taskboard"]}>
      <Routes>
        <Route path="/taskboard" element={<Taskboard />} />
        <Route path="/taskboard/:workItemId" element={<Taskboard />} />
      </Routes>
    </MemoryRouter>,
  );
}

function chips(): string[] {
  const group = screen.queryByRole("group", { name: "Filter by tag" });
  return group
    ? within(group)
        .getAllByRole("button")
        .map((chip) => chip.textContent ?? "")
    : [];
}

async function settle() {
  for (let i = 0; i < 5; i++) {
    await act(async () => {
      await Promise.resolve();
    });
  }
}

describe("Taskboard filter options", () => {
  test("the repository options list every repository, past the server's per-request cap", async () => {
    const repositories = Array.from({ length: 501 }, (_, n) => makeRepo(n));
    const { getRepositories } = mockBoard([], repositories);

    renderTaskboard();

    const select = await screen.findByRole("combobox", { name: "Filter by repository" });
    await waitFor(() => expect(within(select).getAllByRole("option")).toHaveLength(502));
    expect(within(select).getByRole("option", { name: "Repo 0500" })).toBeTruthy();
    expect(getRepositories.mock.calls.map(([opts]) => opts)).toEqual([
      { skip: 0, take: 500 },
      { skip: 500, take: 500 },
    ]);
  });

  test("a tag whose last use is edited away leaves the chips, unless it is selected", async () => {
    const { server, emit } = mockBoard([makeItem("a", ["keep", "gone"]), makeItem("b", ["held"])]);

    renderTaskboard();
    await waitFor(() => expect(chips()).toEqual(["gone", "held", "keep"]));
    fireEvent.click(screen.getByRole("button", { name: "held" }));

    // Another client drops the only use of "gone" and of the selected "held".
    server.items[0] = { ...server.items[0], tags: ["keep"] };
    server.items[1] = { ...server.items[1], tags: [] };
    await emit("WorkItemStateChanged", edited("a"));

    await waitFor(() => expect(chips()).toEqual(["held", "keep"]));
    expect(screen.getByRole("button", { name: "held" }).getAttribute("aria-pressed")).toBe("true");
  });

  test("tags are re-read with one request in flight and one follow-up", async () => {
    const { server, emit } = mockBoard([makeItem("a", ["one"]), makeItem("b", ["two"])]);
    const held: ReturnType<typeof deferred<void>>[] = [];
    let armed = false;
    server.getTags.mockImplementation(async () => {
      if (armed) {
        const hold = deferred<void>();
        held.push(hold);
        await hold.promise;
      }
      return server.tags();
    });

    renderTaskboard();
    await waitFor(() => expect(chips()).toEqual(["one", "two"]));
    armed = true;
    const before = server.getTags.mock.calls.length;

    await emit("WorkItemStateChanged", edited("a"));
    await waitFor(() => expect(server.getTags.mock.calls.length).toBe(before + 1));
    // Changes land while that read is out; its answer predates them.
    server.items[0] = { ...server.items[0], tags: [] };
    await emit("WorkItemStateChanged", edited("a"));
    await emit("WorkItemStateChanged", edited("b"));
    await settle();
    expect(server.getTags.mock.calls.length).toBe(before + 1);

    await act(async () => held[0].resolve());
    await waitFor(() => expect(server.getTags.mock.calls.length).toBe(before + 2));
    await settle();
    expect(server.getTags.mock.calls.length).toBe(before + 2);

    armed = false;
    await act(async () => held[1].resolve());
    await waitFor(() => expect(chips()).toEqual(["two"]));
  });

  test("run, preview and edit-proposal events refresh the card without re-reading the tags", async () => {
    const { server, emit } = mockBoard([makeItem("a", ["one"])]);

    renderTaskboard();
    await waitFor(() => expect(chips()).toEqual(["one"]));
    const before = server.getTags.mock.calls.length;

    server.items[0] = { ...server.items[0], title: "a, next step" };
    await emit("WorkItemRunProgressed", { workItemId: "a" });
    await emit("PreviewStateChanged", { workItemId: "a" });
    await emit("WorkItemEditProposalsChanged", { workItemId: "a" });

    await waitFor(() =>
      expect(screen.getByRole("button", { name: /^a, next step,/ })).toBeTruthy(),
    );
    await settle();
    expect(server.getTags.mock.calls.length).toBe(before);
  });

  test("an edit still re-reads the tags when a run event's fetch supersedes its own", async () => {
    const { server, emit } = mockBoard([makeItem("a", ["one", "gone"])]);

    renderTaskboard();
    await waitFor(() => expect(chips()).toEqual(["gone", "one"]));
    // No delayed re-sync may stand in for the fetch that carries the edit.
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
      server.items[0] = { ...server.items[0], tags: ["one"] };
      const editFetch = deferred<WorkItem>();
      vi.mocked(authServices.workItemService.getById)
        .mockImplementationOnce(() => editFetch.promise)
        .mockImplementation(async (id: string) => ({
          ...server.items.find((wi) => wi.id === id)!,
        }));

      await emit("WorkItemStateChanged", edited("a"));
      await emit("WorkItemRunProgressed", { workItemId: "a" });
      await settle();

      expect(chips()).toEqual(["one"]);
      await act(async () => editFetch.resolve({ ...server.items[0] }));
    } finally {
      vi.useRealTimers();
    }
  });
});

/** The event an edit, or a create, of a Backlog item broadcasts. */
function edited(workItemId: string) {
  return { workItemId, oldStatus: "Backlog", newStatus: "Backlog" };
}
