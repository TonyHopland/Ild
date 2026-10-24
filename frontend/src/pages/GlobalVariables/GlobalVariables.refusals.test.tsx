import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, within, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import * as authServices from "../../services/auth";
import type { GlobalVariableProposal } from "../../types";
import GlobalVariables from "./index";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";

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
      { wrapper: WorkItemHubProvider },
    );

    const row = await screen.findByRole("region", { name: "review_rules" });
    fireEvent.click(within(row).getByRole("button", { name: "Delete" }));
    fireEvent.click(within(row).getByRole("button", { name: /confirm/i }));

    await waitFor(() => expect(getAll).toHaveBeenCalledTimes(2));
    expect(await screen.findByText("No global variables yet.")).toBeTruthy();
  });
});

function suggestion(overrides: Partial<GlobalVariableProposal> = {}): GlobalVariableProposal {
  return {
    id: "gp-1",
    name: "review_rules",
    value: "Keep reviews short. Name the file.",
    rationale: "Reviews miss the file.",
    status: "Pending",
    rejectionReason: null,
    createdAt: "2026-10-03T10:00:00Z",
    decidedAt: null,
    snapshotUpdatedAt: rules.updatedAt,
    currentValue: rules.value,
    variableExists: true,
    createdByLoopRunId: "run-1",
    createdByRunNodeId: null,
    createdByChatSessionId: null,
    chatReplySequence: null,
    source: { kind: "run", workItemId: "wi-7", chatSessionId: null, linkable: true },
    ...overrides,
  };
}

describe("Global Variables page, refused suggestion decisions", () => {
  test("an approve refused because the variable changed says so, though the card leaves the list", async () => {
    vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
      key: "globalVariables.maxLength",
      value: "4000",
    });
    vi.spyOn(authServices.globalVariableService, "getAll").mockResolvedValue([rules]);
    vi.spyOn(authServices.globalVariableService, "listProposals")
      .mockResolvedValueOnce([suggestion()])
      .mockResolvedValue([]);
    vi.spyOn(authServices.globalVariableService, "approveProposal").mockRejectedValue(
      Object.assign(new Error("The variable changed after this suggestion was made."), {
        status: 409,
      }),
    );
    render(
      <MemoryRouter>
        <GlobalVariables />
      </MemoryRouter>,
      { wrapper: WorkItemHubProvider },
    );

    fireEvent.click(await screen.findByRole("button", { name: "Approve" }));

    await waitFor(() => expect(screen.queryByText("Reviews miss the file.")).toBeNull());
    expect(screen.getByRole("alert").textContent).toContain(
      "The variable changed after this suggestion was made.",
    );
  });

  test("with the variables unread, a suggestion for an existing variable is not shown as a new one", async () => {
    vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
      key: "globalVariables.maxLength",
      value: "4000",
    });
    vi.spyOn(authServices.globalVariableService, "getAll").mockRejectedValue(new Error("down"));
    vi.spyOn(authServices.globalVariableService, "listProposals").mockResolvedValue([
      suggestion(),
      suggestion({
        id: "gp-2",
        name: "commit_style",
        rationale: "Nothing says this yet.",
        snapshotUpdatedAt: null,
        currentValue: null,
        variableExists: false,
      }),
    ]);
    render(
      <MemoryRouter>
        <GlobalVariables />
      </MemoryRouter>,
      { wrapper: WorkItemHubProvider },
    );

    const fresh = await screen.findByRole("region", { name: "New variables" });
    expect(within(fresh).queryByText("Reviews miss the file.")).toBeNull();
    expect(within(fresh).getByText("Nothing says this yet.")).toBeTruthy();
    expect(screen.getByText("Reviews miss the file.")).toBeTruthy();
  });
});
