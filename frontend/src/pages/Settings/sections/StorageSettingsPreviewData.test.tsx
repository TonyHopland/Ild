import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import StorageSettings from "./StorageSettings";
import * as services from "../../../services/auth";

const REPO = "55555555-5555-5555-5555-555555555555";

const overview = {
  categories: [
    {
      key: "preview-data",
      label: "Preview data",
      bytes: 2048,
      incomplete: false,
      entries: [
        {
          key: REPO,
          label: "delta-app",
          repositoryId: REPO,
          repositoryName: "delta-app",
          bytes: 2048,
          incomplete: false,
        },
      ],
    },
  ],
};

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => {
    resolve = res;
  });
  return { promise, resolve };
}

const entry = () => screen.getByText("delta-app").closest("li") as HTMLElement;

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("StorageSettings preview data focus and outcome", () => {
  test("focus moves to Confirm, and back to Delete on Cancel", async () => {
    vi.spyOn(services.storageService, "getOverview").mockResolvedValue(overview as never);
    render(<StorageSettings />);
    await screen.findByText("delta-app");

    fireEvent.click(within(entry()).getByRole("button", { name: /delete/i }));
    expect(document.activeElement).toBe(within(entry()).getByRole("button", { name: /confirm/i }));

    fireEvent.click(within(entry()).getByRole("button", { name: "Cancel" }));
    expect(document.activeElement).toBe(within(entry()).getByRole("button", { name: /delete/i }));
  });

  test("a delete says it is done, returns focus to Delete and measures again", async () => {
    const getOverview = vi
      .spyOn(services.storageService, "getOverview")
      .mockResolvedValue(overview as never);
    vi.spyOn(services.storageService, "deletePreviewData").mockResolvedValue();
    render(<StorageSettings />);
    await screen.findByText("delta-app");
    const remeasure = deferred<never>();
    getOverview.mockReturnValueOnce(remeasure.promise);

    fireEvent.click(within(entry()).getByRole("button", { name: /delete/i }));
    fireEvent.click(within(entry()).getByRole("button", { name: /confirm/i }));

    expect((await within(entry()).findByRole("status")).textContent).toBe("Deleted.");
    await waitFor(() =>
      expect(document.activeElement).toBe(within(entry()).getByRole("button", { name: /delete/i })),
    );
    expect(getOverview).toHaveBeenCalledTimes(2);

    remeasure.resolve({ categories: [] } as never);
    await waitFor(() => expect(screen.queryByText("delta-app")).toBeNull());
  });
});
