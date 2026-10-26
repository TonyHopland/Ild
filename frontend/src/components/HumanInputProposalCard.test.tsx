import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor } from "@testing-library/react";
import HumanInputProposalCard from "./HumanInputProposalCard";
import { reply, stubFetch } from "../test-support";
import type { HumanInputProposal, HumanInputProposalStatus } from "../types";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function makeProposal(overrides: Partial<HumanInputProposal> = {}): HumanInputProposal {
  return {
    id: "hp-1",
    workItemId: "wi-1",
    runId: "run-1",
    runNodeId: "exec-1",
    output: "OnSuccess",
    text: "Ship it, but squash first.",
    rationale: "Every check is green.",
    status: "Pending",
    rejectionReason: null,
    decidedBy: null,
    createdAt: "2026-10-01T10:00:00Z",
    decidedAt: null,
    createdByLoopRunId: "run-9",
    createdByChatSessionId: null,
    ...overrides,
  };
}

describe("HumanInputProposalCard", () => {
  test("a pending suggestion shows its text and rationale and offers Approve and Reject, with no way to edit the text", () => {
    const { container } = render(
      <HumanInputProposalCard proposal={makeProposal()} onSettled={vi.fn()} />,
    );

    expect(container.textContent).toContain("Ship it, but squash first.");
    expect(container.textContent).toContain("Every check is green.");
    expect(container.textContent).toContain("Pending");
    expect(screen.getByRole("button", { name: "Approve" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Reject" })).toBeTruthy();
    expect(screen.queryAllByRole("textbox")).toHaveLength(0);
  });

  test("a suggestion without text says No comment", () => {
    const { container } = render(
      <HumanInputProposalCard proposal={makeProposal({ text: "" })} onSettled={vi.fn()} />,
    );

    expect(container.textContent).toContain("No comment");
  });

  test.each<{ output: string; label: RegExp; other: RegExp[] }>([
    { output: "OnSuccess", label: /\bApprove\b/, other: [/\bReject\b/, /OnSuccess/] },
    { output: "OnFailure", label: /\bReject\b/, other: [/\bApprove\b/, /OnFailure/] },
    { output: "Rework", label: /\bRework\b/, other: [/\bApprove\b/, /\bReject\b/] },
  ])("the output $output is shown by the label its button has", ({ output, label, other }) => {
    const { container } = render(
      <HumanInputProposalCard
        proposal={makeProposal({ output, status: "Stale", decidedAt: "2026-10-01T11:00:00Z" })}
        onSettled={vi.fn()}
      />,
    );

    expect(container.textContent).toMatch(label);
    for (const absent of other) expect(container.textContent).not.toMatch(absent);
  });

  test("approving posts the approval of this suggestion alone, then reports it settled", async () => {
    const calls = stubFetch(() => reply(200, { proposal: makeProposal({ status: "Approved" }) }));
    const onSettled = vi.fn();
    render(<HumanInputProposalCard proposal={makeProposal()} onSettled={onSettled} />);

    fireEvent.click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(onSettled).toHaveBeenCalled());
    expect(calls).toHaveLength(1);
    expect(calls[0].method).toBe("POST");
    expect(calls[0].path).toBe("/api/v1/human-input-proposals/hp-1/approve");
  });

  test("an approve refused as stale reports it settled without showing an error", async () => {
    stubFetch(() =>
      reply(409, { error: "The question is no longer open.", proposal: makeProposal() }),
    );
    const onSettled = vi.fn();
    const { container } = render(
      <HumanInputProposalCard proposal={makeProposal()} onSettled={onSettled} />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(onSettled).toHaveBeenCalled());
    expect(container.textContent).not.toContain("The request failed");
  });

  test("rejecting sends the optional reason for this suggestion, then reports it settled", async () => {
    const calls = stubFetch(() => reply(200, makeProposal({ status: "Rejected" })));
    const onSettled = vi.fn();
    render(<HumanInputProposalCard proposal={makeProposal()} onSettled={onSettled} />);

    fireEvent.click(screen.getByRole("button", { name: "Reject" }));
    fireEvent.change(screen.getByRole("textbox", { name: /reason/i }), {
      target: { value: "Squash is not wanted." },
    });
    fireEvent.click(screen.getByRole("button", { name: /confirm/i }));

    await waitFor(() => expect(onSettled).toHaveBeenCalled());
    expect(calls).toHaveLength(1);
    expect(calls[0]).toMatchObject({
      method: "POST",
      path: "/api/v1/human-input-proposals/hp-1/reject",
      body: { reason: "Squash is not wanted." },
    });
  });

  test.each<{ status: HumanInputProposalStatus; reason: string | null }>([
    { status: "Approved", reason: null },
    { status: "Rejected", reason: "Squash is not wanted." },
    { status: "Stale", reason: null },
    { status: "Superseded", reason: null },
  ])("a $status suggestion shows its status and offers no decision", ({ status, reason }) => {
    const { container } = render(
      <HumanInputProposalCard
        proposal={makeProposal({
          status,
          rejectionReason: reason,
          decidedAt: "2026-10-01T11:00:00Z",
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
