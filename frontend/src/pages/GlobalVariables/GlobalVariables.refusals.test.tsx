import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, within, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import * as authServices from "../../services/auth";
import GlobalVariables from "./index";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const rules = {
  id: "g-1",
  name: "review_rules",
  value: "Keep reviews short.",
  updatedAt: "2026-10-01T10:00:00Z",
  usedBy: [],
};

describe("Global Variables page, refused writes", () => {
  test("a delete refused because another tab already deleted it reads the list again", async () => {
    vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
      key: "globalVariables.maxLength",
      value: "4000",
    });
    const getAll = vi
      .spyOn(authServices.globalVariableService, "getAll")
      .mockResolvedValueOnce([rules])
      .mockResolvedValue([]);
    vi.spyOn(authServices.globalVariableService, "delete").mockRejectedValue(
      Object.assign(new Error("No such variable; it may have been deleted."), { status: 404 }),
    );
    render(
      <MemoryRouter>
        <GlobalVariables />
      </MemoryRouter>,
    );

    const row = await screen.findByRole("region", { name: "review_rules" });
    fireEvent.click(within(row).getByRole("button", { name: "Delete" }));
    fireEvent.click(within(row).getByRole("button", { name: /confirm/i }));

    await waitFor(() => expect(getAll).toHaveBeenCalledTimes(2));
    expect(await screen.findByText("No global variables yet.")).toBeTruthy();
  });
});
