import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor } from "@testing-library/react";
import type { ComponentProps } from "react";
import { MemoryRouter, Route, Routes, useLocation, useParams } from "react-router";
import LoopTemplateProposalCard from "./LoopTemplateProposalCard";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

type Proposal = ComponentProps<typeof LoopTemplateProposalCard>["proposal"];

const proposedDocument = JSON.stringify({
  $schema: "ild-loop-template/v2",
  name: "Release Loop",
  description: "Ships a release.",
  recoveryPolicy: "AutoResume",
  nodes: [{ id: "n-build", type: "Cmd", label: "Build", config: { command: "make -j4" } }],
  edges: [],
});

function makeProposal(overrides: Record<string, unknown> = {}): Proposal {
  return {
    id: "lp-1",
    templateId: "tpl-1",
    templateName: "Release Loop",
    templateExists: true,
    baseVersion: 7,
    latestVersion: 7,
    rationale: "Builds keep failing on the slow runner.",
    status: "Pending",
    editedBeforeSave: false,
    appliedVersion: null,
    rejectionReason: null,
    createdAt: "2026-10-01T10:00:00Z",
    decidedAt: null,
    createdByLoopRunId: "run-1",
    createdByRunNodeId: "exec-1",
    createdByChatSessionId: null,
    chatReplySequence: null,
    source: { kind: "run", workItemId: "wi-1", chatSessionId: null, linkable: true },
    document: proposedDocument,
    changes: {
      nameChanged: false,
      descriptionChanged: false,
      recoveryPolicyChanged: false,
      nodesAdded: [{ id: "n-lint", label: "Lint", type: "Cmd" }],
      nodesRemoved: [{ id: "n-test", label: "Unit Tests", type: "Cmd" }],
      nodesChanged: [{ id: "n-build", label: "Build", type: "Cmd", fields: ["config.command"] }],
      edgesAdded: [
        { sourceLabel: "Build", targetLabel: "Lint", edgeType: "OnSuccess", name: null },
      ],
      edgesRemoved: [
        { sourceLabel: "Build", targetLabel: "Unit Tests", edgeType: "OnSuccess", name: null },
      ],
    },
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

/** Where a navigation from the card ended up. */
function EditorProbe() {
  const { templateId } = useParams();
  const location = useLocation();
  return (
    <div data-testid="editor-probe">
      {templateId}
      {location.search}
    </div>
  );
}

function renderCard(proposal: Proposal, onSettled = vi.fn()) {
  return render(
    <MemoryRouter initialEntries={["/somewhere"]}>
      <Routes>
        <Route
          path="/somewhere"
          element={<LoopTemplateProposalCard proposal={proposal} onSettled={onSettled} />}
        />
        <Route path="/loop-editor/:templateId" element={<EditorProbe />} />
      </Routes>
    </MemoryRouter>,
  );
}

function reviewControl(): HTMLElement | null {
  return (
    screen.queryByRole("link", { name: /^review$/i }) ??
    screen.queryByRole("button", { name: /^review$/i })
  );
}

describe("LoopTemplateProposalCard", () => {
  test("shows the template, its base version, the rationale and what changes structurally, never the raw document", () => {
    const { container } = renderCard(makeProposal());
    const text = container.textContent ?? "";

    expect(text).toContain("Release Loop");
    expect(text).toMatch(/(^|[^0-9])7([^0-9]|$)/);
    expect(text).toContain("Builds keep failing on the slow runner.");
    expect(text).toContain("Lint");
    expect(text).toContain("Unit Tests");
    expect(text).toContain("Build");
    expect(text).not.toContain("ild-loop-template/v2");
    expect(text).not.toContain("make -j4");
    expect(text).not.toContain('"nodes"');
    expect(screen.getByRole("button", { name: "Save" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Reject" })).toBeTruthy();
    expect(reviewControl()).toBeTruthy();
  });

  test("saving posts the save of this proposal alone, then reports it settled", async () => {
    const calls = stubFetch({ status: 200, body: { proposal: {}, version: 8 } });
    const onSettled = vi.fn();
    renderCard(makeProposal(), onSettled);

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(onSettled).toHaveBeenCalled());
    expect(calls).toHaveLength(1);
    expect(calls[0].method).toBe("POST");
    expect(calls[0].path).toBe("/api/v1/loop-template-proposals/lp-1/save");
  });

  test("a save refused because the template moved on still reports it settled, so the owner re-reads", async () => {
    stubFetch({
      status: 409,
      body: { error: "The template has a newer version.", proposal: { status: "Stale" } },
    });
    const onSettled = vi.fn();
    renderCard(makeProposal(), onSettled);

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(onSettled).toHaveBeenCalled());
  });

  test("rejecting sends the optional reason for this proposal, then reports it settled", async () => {
    const calls = stubFetch({ status: 200, body: makeProposal({ status: "Rejected" }) });
    const onSettled = vi.fn();
    renderCard(makeProposal(), onSettled);

    fireEvent.click(screen.getByRole("button", { name: "Reject" }));
    fireEvent.change(screen.getByRole("textbox", { name: /reason/i }), {
      target: { value: "Keep one build job." },
    });
    fireEvent.click(screen.getByRole("button", { name: /confirm/i }));

    await waitFor(() => expect(onSettled).toHaveBeenCalled());
    expect(calls).toHaveLength(1);
    expect(calls[0]).toMatchObject({
      method: "POST",
      path: "/api/v1/loop-template-proposals/lp-1/reject",
      body: { reason: "Keep one build job." },
    });
  });

  test("Review opens the Loop Editor on the template with this proposal", async () => {
    stubFetch({ status: 200, body: [] });
    renderCard(makeProposal());

    fireEvent.click(reviewControl()!);

    const probe = await screen.findByTestId("editor-probe");
    expect(probe.textContent).toContain("tpl-1");
    expect(
      new URLSearchParams(probe.textContent!.slice(probe.textContent!.indexOf("?"))).get(
        "proposal",
      ),
    ).toBe("lp-1");
  });

  test.each([
    { status: "Applied", edited: false, version: 8, reason: null, shows: /as is/i },
    { status: "Applied", edited: true, version: 8, reason: null, shows: /edit/i },
    {
      status: "Rejected",
      edited: false,
      version: null,
      reason: "Keep one build job.",
      shows: /Keep one build job\./,
    },
    { status: "Stale", edited: false, version: null, reason: null, shows: /Stale/ },
    { status: "Superseded", edited: false, version: null, reason: null, shows: /Superseded/ },
  ])(
    "a $status proposal (edited: $edited) shows its outcome and offers no Save, Reject or Review",
    ({ status, edited, version, reason, shows }) => {
      const { container } = renderCard(
        makeProposal({
          status,
          editedBeforeSave: edited,
          appliedVersion: version,
          rejectionReason: reason,
          decidedAt: "2026-10-01T11:00:00Z",
        }),
      );
      const text = container.textContent ?? "";

      expect(text).toContain(status);
      expect(text).toMatch(shows);
      if (version !== null) expect(text).toMatch(/(^|[^0-9])8([^0-9]|$)/);
      expect(screen.queryByRole("button", { name: "Save" })).toBeNull();
      expect(screen.queryByRole("button", { name: "Reject" })).toBeNull();
      expect(reviewControl()).toBeNull();
    },
  );
});
