import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import PreviewDataDelete, { PreviewDataDeleteWording } from "./PreviewDataDelete";
import * as services from "../services/auth";

const wording: PreviewDataDeleteWording = {
  trigger: "Reset preview data",
  confirmGroup: "Reset preview data",
  warning: "This wipes the preview data for every work item in alpha-app.",
  confirm: "Confirm reset",
  done: "The preview data was reset.",
  failed: "Could not reset the preview data.",
};

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("PreviewDataDelete", () => {
  test("focus moves to Confirm, then back to the trigger once it is answered, and a failure can be retried", async () => {
    const remove = vi
      .spyOn(services.storageService, "deletePreviewData")
      .mockRejectedValueOnce(new Error("A preview is already running."))
      .mockResolvedValueOnce();
    const onDeleted = vi.fn();
    render(<PreviewDataDelete repositoryId="repo-1" wording={wording} onDeleted={onDeleted} />);

    fireEvent.click(screen.getByRole("button", { name: "Reset preview data" }));
    expect(document.activeElement).toBe(screen.getByRole("button", { name: "Confirm reset" }));
    fireEvent.click(screen.getByRole("button", { name: "Confirm reset" }));

    expect((await screen.findByRole("alert")).textContent).toBe("A preview is already running.");
    await waitFor(() =>
      expect(document.activeElement).toBe(
        screen.getByRole("button", { name: "Reset preview data" }),
      ),
    );
    expect(onDeleted).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Reset preview data" }));
    fireEvent.click(screen.getByRole("button", { name: "Confirm reset" }));

    expect((await screen.findByRole("status")).textContent).toBe("The preview data was reset.");
    await waitFor(() =>
      expect(document.activeElement).toBe(
        screen.getByRole("button", { name: "Reset preview data" }),
      ),
    );
    expect(screen.queryByRole("alert")).toBeNull();
    expect(remove).toHaveBeenCalledTimes(2);
    expect(remove).toHaveBeenLastCalledWith("repo-1");
    expect(onDeleted).toHaveBeenCalledTimes(1);
  });

  test("Cancel sends nothing and puts focus back on the trigger", () => {
    const remove = vi.spyOn(services.storageService, "deletePreviewData");
    render(<PreviewDataDelete repositoryId="repo-1" wording={wording} />);

    fireEvent.click(screen.getByRole("button", { name: "Reset preview data" }));
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(document.activeElement).toBe(screen.getByRole("button", { name: "Reset preview data" }));
    expect(remove).not.toHaveBeenCalled();
  });
});
