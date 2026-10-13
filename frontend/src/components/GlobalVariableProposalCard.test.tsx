import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor } from "@testing-library/react";
import type { ComponentProps } from "react";
import GlobalVariableProposalCard from "./GlobalVariableProposalCard";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

type Proposal = ComponentProps<typeof GlobalVariableProposalCard>["proposal"];

function makeProposal(overrides: Record<string, unknown> = {}): Proposal {
  return {
    id: "gp-1",
    name: "review_rules",
    value: "Keep reviews short.\nName the file and the line.",
    rationale: "Reviews keep missing where the problem is.",
    status: "Pending",
    rejectionReason: null,
    createdAt: "2026-10-01T10:00:00Z",
    decidedAt: null,
    snapshotUpdatedAt: "2026-09-30T10:00:00Z",
    currentValue: "Keep reviews short.\nName the file.",
    variableExists: true,
    createdByLoopRunId: "run-1",
    createdByRunNodeId: "exec-1",
    createdByChatSessionId: null,
    chatReplySequence: null,
    source: { kind: "run", workItemId: "wi-1", chatSessionId: null, linkable: true },
    ...overrides,
  } as unknown as Proposal;
}

interface Call {
  method: string;
  path: string;
  body: unknown;
}

/** Answers every request with `reply`, recording each. */
function stubFetch(reply: { status: number; body?: unknown }) {
  const calls: Call[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: string, init?: RequestInit) => {
      calls.push({
        method: (init?.method ?? "GET").toUpperCase(),
        path: new URL(input, "http://localhost").pathname,
        body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined,
      });
      return {
        ok: reply.status < 400,
        status: reply.status,
        statusText: "",
        text: () => Promise.resolve(reply.body === undefined ? "" : JSON.stringify(reply.body)),
      } as Response;
    }),
  );
  return calls;
}

describe("GlobalVariableProposalCard", () => {
  test("shows the variable, its current text against the suggested text and the rationale, with no way to edit the text", () => {
    const { container } = render(
      <GlobalVariableProposalCard proposal={makeProposal()} onSettled={vi.fn()} />,
    );
    const text = container.textContent ?? "";

    expect(text).toContain("review_rules");
    expect(text).toContain("Name the file.");
    expect(text).toContain("Name the file and the line.");
    expect(text).toContain("Reviews keep missing where the problem is.");
    expect(text).not.toMatch(/new variable/i);
    expect(screen.getByRole("button", { name: "Approve" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Reject" })).toBeTruthy();
    expect(screen.queryAllByRole("textbox")).toHaveLength(0);
  });

  test("a suggestion for a variable that does not exist says it is a new variable", () => {
    const { container } = render(
      <GlobalVariableProposalCard
        proposal={makeProposal({
          name: "commit_style",
          value: "Imperative mood.",
          currentValue: null,
          variableExists: false,
          snapshotUpdatedAt: null,
        })}
        onSettled={vi.fn()}
      />,
    );

    expect(container.textContent).toMatch(/new variable/i);
    expect(container.textContent).toContain("commit_style");
    expect(container.textContent).toContain("Imperative mood.");
  });

  test("approving posts the approval of this suggestion alone, then reports it settled", async () => {
    const calls = stubFetch({ status: 200, body: { proposal: {}, variable: {} } });
    const onSettled = vi.fn();
    render(<GlobalVariableProposalCard proposal={makeProposal()} onSettled={onSettled} />);

    fireEvent.click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(onSettled).toHaveBeenCalled());
    expect(calls).toHaveLength(1);
    expect(calls[0].method).toBe("POST");
    expect(calls[0].path).toBe("/api/v1/global-variables/proposals/gp-1/approve");
    expect(calls[0].body === undefined || !("value" in (calls[0].body as object))).toBe(true);
  });

  test("an approve refused as stale still reports it settled, so the owner re-reads", async () => {
    stubFetch({
      status: 409,
      body: { error: "The variable changed after this suggestion was made.", proposal: {} },
    });
    const onSettled = vi.fn();
    render(<GlobalVariableProposalCard proposal={makeProposal()} onSettled={onSettled} />);

    fireEvent.click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(onSettled).toHaveBeenCalled());
  });

  test("rejecting sends the optional reason for this suggestion, then reports it settled", async () => {
    const calls = stubFetch({ status: 200, body: makeProposal({ status: "Rejected" }) });
    const onSettled = vi.fn();
    render(<GlobalVariableProposalCard proposal={makeProposal()} onSettled={onSettled} />);

    fireEvent.click(screen.getByRole("button", { name: "Reject" }));
    fireEvent.change(screen.getByRole("textbox", { name: /reason/i }), {
      target: { value: "Too vague." },
    });
    fireEvent.click(screen.getByRole("button", { name: /confirm/i }));

    await waitFor(() => expect(onSettled).toHaveBeenCalled());
    expect(calls).toHaveLength(1);
    expect(calls[0]).toMatchObject({
      method: "POST",
      path: "/api/v1/global-variables/proposals/gp-1/reject",
      body: { reason: "Too vague." },
    });
  });

  test.each([
    { status: "Approved", reason: null },
    { status: "Rejected", reason: "Too vague." },
    { status: "Stale", reason: null },
    { status: "Superseded", reason: null },
  ])("a $status suggestion shows its status and offers no decision", ({ status, reason }) => {
    const { container } = render(
      <GlobalVariableProposalCard
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
