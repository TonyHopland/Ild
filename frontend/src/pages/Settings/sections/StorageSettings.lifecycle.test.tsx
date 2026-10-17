import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import StorageSettings from "./StorageSettings";
import { storageService } from "../../../services/auth";
import type { StorageOverview, WorktreeCleanupCandidates } from "../../../types";

const overview: StorageOverview = {
  categories: [
    { key: "worktrees", label: "Worktrees", bytes: 4096, incomplete: false, entries: [] },
  ],
};

const candidates: WorktreeCleanupCandidates = {
  candidates: [
    {
      runId: "aaaaaaaa-0000-0000-0000-000000000001",
      workItemId: "101",
      workItemTitle: "Fix login redirect",
      repositoryId: null,
      repositoryName: null,
      bytes: 4096,
      incomplete: false,
    },
  ],
  totalBytes: 4096,
};

const never = () => new Promise<never>(() => {});
const button = (name: RegExp | string) => screen.getByRole("button", { name }) as HTMLButtonElement;

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("StorageSettings lifecycle", () => {
  test("leaving the section aborts the measurement still running", async () => {
    const getOverview = vi.spyOn(storageService, "getOverview").mockResolvedValueOnce(overview);
    render(<StorageSettings />);
    await screen.findByText("Worktrees");

    getOverview.mockImplementation(never);
    fireEvent.click(button("Refresh"));
    const refresh = getOverview.mock.calls[1][0] as AbortSignal;
    expect(refresh.aborted).toBe(false);

    cleanup();

    expect(refresh.aborted).toBe(true);
  });

  test("measuring does not hold up the cleanup: Confirm and Cancel stay usable during a Refresh", async () => {
    const getOverview = vi.spyOn(storageService, "getOverview").mockResolvedValueOnce(overview);
    vi.spyOn(storageService, "getCleanupCandidates").mockResolvedValue(candidates);
    render(<StorageSettings />);
    await screen.findByText("Worktrees");
    fireEvent.click(button("Clean up worktrees of Done work items"));
    await screen.findByText(/Fix login redirect/);

    getOverview.mockImplementation(never);
    fireEvent.click(button("Refresh"));

    expect(button("Refresh").disabled).toBe(true);
    expect(button(/confirm/i).disabled).toBe(false);
    expect(button(/cancel/i).disabled).toBe(false);
  });

  test("focus moves to the list to confirm, and back to the cleanup button when it closes", async () => {
    vi.spyOn(storageService, "getOverview").mockResolvedValue(overview);
    vi.spyOn(storageService, "getCleanupCandidates").mockResolvedValue(candidates);
    render(<StorageSettings />);
    await screen.findByText("Worktrees");
    fireEvent.click(button("Clean up worktrees of Done work items"));

    const list = await screen.findByRole("group", { name: /will be removed/i });
    expect(document.activeElement).toBe(list);

    fireEvent.click(button(/cancel/i));

    await waitFor(() =>
      expect(document.activeElement).toBe(button("Clean up worktrees of Done work items")),
    );
  });
});
