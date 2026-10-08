import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, act, waitFor } from "@testing-library/react";
import EditProposalCard from "./EditProposalCard";
import type { WorkItemEditProposal } from "../types";
import * as authServices from "../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function makeProposal(overrides: Partial<WorkItemEditProposal> = {}): WorkItemEditProposal {
  return {
    id: "p-1",
    workItemId: "wi-1",
    status: "Pending",
    proposed: {
      title: "Sharper title",
      description: null,
      tags: ["kept-tag", "added-tag"],
      branchNameOverride: null,
      baseBranchOverride: "",
    },
    snapshot: {
      title: "Old title",
      description: "An untouched description.",
      tags: ["kept-tag", "dropped-tag"],
      branchNameOverride: "feature/untouched-branch",
      baseBranchOverride: "develop-base",
    },
    rationale: "The old title no longer says what the item is about.",
    rejectionReason: null,
    createdByLoopRunId: null,
    createdByChatSessionId: "chat-1",
    createdAt: "2026-09-26T10:00:00Z",
    decidedAt: null,
    ...overrides,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const approveButton = () => screen.getByRole("button", { name: "Approve" });
const rejectButton = () => screen.getByRole("button", { name: "Reject" });

describe("EditProposalCard", () => {
  test("shows current against proposed for the proposed fields only", () => {
    const { container } = render(
      <EditProposalCard proposal={makeProposal()} onSettled={vi.fn()} />,
    );
    const text = container.textContent ?? "";

    expect(text).toContain("Old title");
    expect(text).toContain("Sharper title");
    expect(text).toContain("dropped-tag");
    expect(text).toContain("added-tag");
    // The cleared base branch shows what it was cleared from.
    expect(text).toContain("develop-base");
    expect(text).toContain("The old title no longer says what the item is about.");

    // Neither the description nor the custom branch was proposed.
    expect(text).not.toContain("An untouched description.");
    expect(text).not.toContain("feature/untouched-branch");
  });

  test("shows each dependency change as a line naming the item, and no title it does not know", () => {
    const { container } = render(
      <EditProposalCard
        proposal={makeProposal({
          proposed: {
            addDependencies: [{ id: "wi-277", title: "Chat tab" }],
            removeDependencies: [
              { id: "wi-12", title: "Old plan" },
              { id: "wi-9", title: null },
            ],
          },
          snapshot: { ...makeProposal().snapshot, dependencies: ["wi-12", "wi-9"] },
        })}
        onSettled={vi.fn()}
      />,
    );
    const text = (container.textContent ?? "").replace(/\s+/g, " ");

    expect(text).toMatch(/Add dependency: ?#wi-277 ?Chat tab/);
    expect(text).toMatch(/Remove dependency: ?#wi-12 ?Old plan/);

    const goneLines = Array.from(container.querySelectorAll("*")).filter(
      (el) =>
        /Remove dependency/.test(el.textContent ?? "") &&
        /#wi-9/.test(el.textContent ?? "") &&
        !/wi-12/.test(el.textContent ?? ""),
    );
    expect(goneLines.length).toBeGreaterThan(0);
    const line = goneLines[goneLines.length - 1].textContent ?? "";
    expect(line.replace(/\s+/g, " ").trim()).toMatch(/^Remove dependency: ?#wi-9$/);

    // Nothing else was proposed.
    expect(text).not.toContain("Old title");
    expect(text).not.toContain("develop-base");
  });

  test("approving calls the approve endpoint for this proposal, then reports it settled", async () => {
    const approval = deferred<unknown>();
    const approve = vi
      .spyOn(authServices.workItemService, "approveEditProposal")
      .mockReturnValue(
        approval.promise as ReturnType<typeof authServices.workItemService.approveEditProposal>,
      );
    const onSettled = vi.fn();
    render(<EditProposalCard proposal={makeProposal()} onSettled={onSettled} />);

    fireEvent.click(approveButton());

    expect(approve).toHaveBeenCalledWith("wi-1", "p-1");
    // One decision at a time: both buttons wait for the answer.
    expect((approveButton() as HTMLButtonElement).disabled).toBe(true);
    expect((rejectButton() as HTMLButtonElement).disabled).toBe(true);
    expect(onSettled).not.toHaveBeenCalled();

    await act(async () => {
      approval.resolve({});
      await approval.promise;
    });
    await waitFor(() => expect(onSettled).toHaveBeenCalled());
  });

  test("an approve refused as stale still reports it settled, so the owner re-reads", async () => {
    vi.spyOn(authServices.workItemService, "approveEditProposal").mockRejectedValue({
      status: 409,
      message: "The work item changed after this proposal was made.",
    });
    const onSettled = vi.fn();
    render(<EditProposalCard proposal={makeProposal()} onSettled={onSettled} />);

    fireEvent.click(approveButton());

    await waitFor(() => expect(onSettled).toHaveBeenCalled());
  });

  test("rejecting sends the optional reason for this proposal, then reports it settled", async () => {
    const reject = vi
      .spyOn(authServices.workItemService, "rejectEditProposal")
      .mockResolvedValue(makeProposal({ status: "Rejected", rejectionReason: "Too vague." }));
    const approve = vi.spyOn(authServices.workItemService, "approveEditProposal");
    const onSettled = vi.fn();
    render(<EditProposalCard proposal={makeProposal()} onSettled={onSettled} />);

    fireEvent.click(rejectButton());
    fireEvent.change(screen.getByRole("textbox", { name: /reason/i }), {
      target: { value: "Too vague." },
    });
    fireEvent.click(screen.getByRole("button", { name: "Confirm reject" }));

    await waitFor(() => expect(onSettled).toHaveBeenCalled());
    expect(reject).toHaveBeenCalledWith("wi-1", "p-1", "Too vague.");
    expect(approve).not.toHaveBeenCalled();
  });

  test.each([
    { status: "Approved" as const, reason: null },
    { status: "Rejected" as const, reason: "Too vague." },
    { status: "Stale" as const, reason: null },
  ])("a $status proposal shows its status and offers no decision", ({ status, reason }) => {
    const { container } = render(
      <EditProposalCard
        proposal={makeProposal({
          status,
          rejectionReason: reason,
          decidedAt: "2026-09-26T11:00:00Z",
        })}
        onSettled={vi.fn()}
      />,
    );

    expect(container.textContent).toContain(status);
    if (reason) expect(container.textContent).toContain(reason);
    expect(screen.queryByRole("button", { name: "Approve" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Reject" })).toBeNull();
  });
});
