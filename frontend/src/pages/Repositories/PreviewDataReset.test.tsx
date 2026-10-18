import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import PreviewDataReset from "./PreviewDataReset";
import * as services from "../../services/auth";
import { Repository, WorkItemStatus } from "../../types";

const repository: Repository = {
  id: "repo-1",
  name: "alpha-app",
  remoteProviderId: "prov-1",
  cloneUrl: "https://git.example.com/alpha-app.git",
  defaultBranch: "main",
  worktreesPath: null,
  defaultIntakeStatus: WorkItemStatus.Backlog,
  keepPreviewData: true,
  createdAt: "2025-01-01T00:00:00Z",
};

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("PreviewDataReset", () => {
  test("focus moves to Confirm, then back to Reset once it is answered, and a failure can be retried", async () => {
    const reset = vi
      .spyOn(services.repositoryService, "resetPreviewData")
      .mockRejectedValueOnce(new Error("A preview is already running."))
      .mockResolvedValueOnce();
    render(<PreviewDataReset repository={repository} />);

    fireEvent.click(screen.getByRole("button", { name: "Reset preview data" }));
    expect(document.activeElement).toBe(screen.getByRole("button", { name: "Confirm reset" }));
    fireEvent.click(screen.getByRole("button", { name: "Confirm reset" }));

    expect((await screen.findByRole("alert")).textContent).toBe("A preview is already running.");
    expect(document.activeElement).toBe(screen.getByRole("button", { name: "Reset preview data" }));

    fireEvent.click(screen.getByRole("button", { name: "Reset preview data" }));
    fireEvent.click(screen.getByRole("button", { name: "Confirm reset" }));

    expect((await screen.findByRole("status")).textContent).toBe("The preview data was reset.");
    expect(screen.queryByRole("alert")).toBeNull();
    expect(reset).toHaveBeenCalledTimes(2);
  });
});
