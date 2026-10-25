import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import StorageSettings from "./StorageSettings";
import * as services from "../../../services/auth";

const KB = 1024;
const MB = 1024 * KB;
const GB = 1024 * MB;

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

function overview(incomplete: boolean, worktreesBytes = 5 * GB) {
  return {
    databases: [],
    categories: [
      {
        key: "worktrees",
        label: "Worktrees",
        bytes: worktreesBytes,
        incomplete,
        entries: [
          {
            key: "11111111-1111-1111-1111-111111111111",
            label: "alpha",
            repositoryId: "11111111-1111-1111-1111-111111111111",
            repositoryName: "alpha",
            bytes: 3 * GB,
            incomplete,
          },
          {
            key: "unowned",
            label: "Not owned by any run",
            repositoryId: null,
            repositoryName: null,
            bytes: worktreesBytes - 3 * GB,
            incomplete: false,
          },
        ],
      },
      {
        key: "temp",
        label: "Temp",
        bytes: 3 * MB,
        incomplete: false,
        entries: [
          {
            key: "private",
            label: "Orchestrator private",
            repositoryId: null,
            repositoryName: null,
            bytes: 3 * MB,
            incomplete: false,
          },
          {
            key: "scratch",
            label: "Agent scratch",
            repositoryId: null,
            repositoryName: null,
            bytes: null,
            incomplete: false,
          },
        ],
      },
      {
        key: "other",
        label: "Other ILD data",
        bytes: 12 * KB,
        incomplete: false,
        entries: [],
      },
    ],
  };
}

const refreshButton = () => screen.getByRole("button", { name: /refresh/i }) as HTMLButtonElement;
const pageText = () => document.body.textContent ?? "";

/** Marks of an incomplete figure, whether written out or carried by a title/label. */
function incompleteMarks() {
  return (
    screen.queryAllByText(/incomplete/i).length +
    document.querySelectorAll('[title*="ncomplete" i], [aria-label*="ncomplete" i]').length
  );
}

function spyServices() {
  const storage = (services as any).storageService;
  // The backup section has its own tests; here it only has to have something to show.
  vi.spyOn(storage, "getBackupStatus").mockResolvedValue({
    databases: {
      core: { available: false, reason: "No backup in this test." },
      workitems: { available: false, reason: "No backup in this test." },
    },
    secretKeyConfigured: true,
    minPassphraseLength: 12,
  } as never);
  return {
    getOverview: vi.spyOn(storage, "getOverview"),
  };
}

async function renderLoaded() {
  const spies = spyServices();
  spies.getOverview.mockResolvedValue(overview(false) as never);
  render(<StorageSettings />);
  await screen.findAllByText(/Worktrees/);
  return spies;
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("StorageSettings overview", () => {
  test("shows a loading state while measuring, then each category with its size and breakdown", async () => {
    const spies = spyServices();
    const load = deferred<unknown>();
    spies.getOverview.mockReturnValue(load.promise as never);
    render(<StorageSettings />);

    expect(screen.getAllByText(/measuring|loading/i).length).toBeGreaterThan(0);
    expect(pageText()).not.toMatch(/\b5(\.0)? GB\b/);

    load.resolve(overview(false));

    expect(await screen.findAllByText(/Worktrees/)).toBeTruthy();
    expect(screen.queryAllByText(/measuring|loading/i)).toEqual([]);
    expect(screen.getAllByText(/Other ILD data/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/alpha/).length).toBeGreaterThan(0);
    expect(pageText()).toMatch(/\b5(\.0)? GB\b/);
    expect(pageText()).toMatch(/\b3(\.0)? GB\b/);
    expect(pageText()).toMatch(/\b12 KB\b/);
  });

  test("says a null figure was not measured rather than showing a number for it", async () => {
    await renderLoaded();

    expect(screen.getAllByText(/not measured/i).length).toBeGreaterThan(0);
    expect(pageText()).not.toMatch(/\bnull\b|NaN|undefined/);
  });

  test("marks incomplete figures", async () => {
    const spies = spyServices();
    spies.getOverview.mockResolvedValue(overview(false) as never);
    render(<StorageSettings />);
    await screen.findAllByText(/Worktrees/);
    const marksWhenComplete = incompleteMarks();
    cleanup();

    spies.getOverview.mockResolvedValue(overview(true) as never);
    render(<StorageSettings />);
    await screen.findAllByText(/Worktrees/);

    expect(incompleteMarks()).toBeGreaterThan(marksWhenComplete);
  });

  test("shows an error when measuring fails", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const spies = spyServices();
    spies.getOverview.mockRejectedValue({ status: 500, message: "Disk scan exploded" });
    render(<StorageSettings />);

    expect(
      (await screen.findAllByText(/disk scan exploded|fail|error|could not/i)).length,
    ).toBeGreaterThan(0);
    expect(screen.queryAllByText(/measuring|loading/i)).toEqual([]);
    expect(pageText()).not.toMatch(/\b5(\.0)? GB\b/);
  });

  test("offers no bulk cleanup of Done items' worktrees: a run's worktree goes when the run ends", async () => {
    await renderLoaded();

    expect(screen.queryByRole("button", { name: /clean up worktrees/i })).toBeNull();
    expect(screen.queryByText(/worktrees of done work items/i)).toBeNull();
  });

  test("Refresh measures again and shows the new figures", async () => {
    const spies = await renderLoaded();
    expect(pageText()).toMatch(/\b5(\.0)? GB\b/);

    spies.getOverview.mockResolvedValue(overview(false, 7 * GB) as never);
    fireEvent.click(refreshButton());

    await waitFor(() => expect(pageText()).toMatch(/\b7(\.0)? GB\b/));
    expect(spies.getOverview).toHaveBeenCalledTimes(2);
  });
});

const KEPT_REPO = "33333333-3333-3333-3333-333333333333";
const DELETED_REPO = "44444444-4444-4444-4444-444444444444";

function overviewWithPreviewData() {
  const base = overview(false);
  return {
    databases: [],
    categories: [
      ...base.categories,
      {
        key: "preview-data",
        label: "Preview data",
        bytes: 3 * MB,
        incomplete: false,
        entries: [
          {
            key: KEPT_REPO,
            label: "gamma-app",
            repositoryId: KEPT_REPO,
            repositoryName: "gamma-app",
            bytes: 2 * MB,
            incomplete: false,
          },
          {
            key: DELETED_REPO,
            label: "Repository no longer exists",
            repositoryId: DELETED_REPO,
            repositoryName: null,
            bytes: 1 * MB,
            incomplete: false,
          },
          {
            key: "unowned",
            label: "Not a repository's preview data",
            repositoryId: null,
            repositoryName: null,
            bytes: 0,
            incomplete: false,
          },
        ],
      },
    ],
  };
}

/** The DELETE goes through the real storage service; everything else is spied. */
function stubDeleteFetch(response: { status: number; body: unknown }) {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: response.status < 400,
    status: response.status,
    text: () => Promise.resolve(JSON.stringify(response.body)),
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function deleteCalls(fetchMock: ReturnType<typeof vi.fn>) {
  return fetchMock.mock.calls.filter(
    (c: unknown[]) => (c[1] as { method?: string } | undefined)?.method === "DELETE",
  );
}

const entryRow = (label: string) => screen.getByText(label).closest("li") as HTMLElement;

async function renderWithPreviewData() {
  const spies = spyServices();
  spies.getOverview.mockResolvedValue(overviewWithPreviewData() as never);
  render(<StorageSettings />);
  await screen.findByText("gamma-app");
  return spies;
}

describe("StorageSettings preview data", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  test("each repository's preview data has a Delete that asks first, deletes that folder and measures again", async () => {
    const fetchMock = stubDeleteFetch({ status: 200, body: {} });
    const spies = await renderWithPreviewData();

    // Only the preview-data entries that name a repository can be deleted.
    expect(screen.getAllByRole("button", { name: /delete/i })).toHaveLength(2);
    expect(
      within(entryRow("Not a repository's preview data")).queryByRole("button", {
        name: /delete/i,
      }),
    ).toBeNull();

    fireEvent.click(within(entryRow("gamma-app")).getByRole("button", { name: /delete/i }));
    expect(deleteCalls(fetchMock)).toHaveLength(0);

    fireEvent.click(screen.getByRole("button", { name: /confirm/i }));

    await waitFor(() => expect(deleteCalls(fetchMock)).toHaveLength(1));
    expect(deleteCalls(fetchMock)[0][0]).toMatch(new RegExp(`/storage/preview-data/${KEPT_REPO}$`));
    await waitFor(() => expect(spies.getOverview).toHaveBeenCalledTimes(2));
  });

  test("the folder of a deleted repository can be deleted too", async () => {
    const fetchMock = stubDeleteFetch({ status: 200, body: {} });
    await renderWithPreviewData();

    fireEvent.click(
      within(entryRow("Repository no longer exists")).getByRole("button", { name: /delete/i }),
    );
    fireEvent.click(screen.getByRole("button", { name: /confirm/i }));

    await waitFor(() => expect(deleteCalls(fetchMock)).toHaveLength(1));
    expect(deleteCalls(fetchMock)[0][0]).toMatch(
      new RegExp(`/storage/preview-data/${DELETED_REPO}$`),
    );
  });

  test("a refused delete shows the reason", async () => {
    const refusal =
      "A preview is already running for work item 305 (Title). Stop it before resetting the preview data.";
    stubDeleteFetch({ status: 409, body: { error: refusal } });
    await renderWithPreviewData();

    fireEvent.click(within(entryRow("gamma-app")).getByRole("button", { name: /delete/i }));
    fireEvent.click(screen.getByRole("button", { name: /confirm/i }));

    await waitFor(() =>
      expect(screen.getAllByRole("alert").some((a) => a.textContent?.includes(refusal))).toBe(true),
    );
  });
});
