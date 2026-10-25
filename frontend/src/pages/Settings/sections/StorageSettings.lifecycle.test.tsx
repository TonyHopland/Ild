import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import StorageSettings from "./StorageSettings";
import { storageService } from "../../../services/auth";
import type { StorageOverview } from "../../../types";

const overview: StorageOverview = {
  databases: [],
  categories: [
    { key: "worktrees", label: "Worktrees", bytes: 4096, incomplete: false, entries: [] },
  ],
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
});
