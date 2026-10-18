import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation, useParams } from "react-router";
import LoopTemplateProposalCard from "./LoopTemplateProposalCard";
import { makeLoopTemplateProposal, reply, stubFetch } from "../test-support";
import type { LoopTemplateEdit, LoopTemplateProposal, LoopTemplateProposalStatus } from "../types";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const proposedDocument = JSON.stringify({
  $schema: "ild-loop-template/v2",
  name: "Release Loop",
  description: "Ships a release.",
  recoveryPolicy: "AutoResume",
  nodes: [{ id: "n-build", type: "Cmd", label: "Build", config: { command: "make -j4" } }],
  edges: [],
});

function makeProposal(overrides: Partial<LoopTemplateProposal> = {}): LoopTemplateProposal {
  return makeLoopTemplateProposal({
    baseVersion: 7,
    latestVersion: 7,
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
  });
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

function renderCard(proposal: LoopTemplateProposal, onSettled = vi.fn()) {
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
    const calls = stubFetch(() => reply(200, { proposal: {}, version: 8 }));
    const onSettled = vi.fn();
    renderCard(makeProposal(), onSettled);

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(onSettled).toHaveBeenCalled());
    expect(calls).toHaveLength(1);
    expect(calls[0].method).toBe("POST");
    expect(calls[0].path).toBe("/api/v1/loop-template-proposals/lp-1/save");
  });

  test("a save refused because the template moved on still reports it settled, so the owner re-reads", async () => {
    stubFetch(() =>
      reply(409, { error: "The template has a newer version.", proposal: { status: "Stale" } }),
    );
    const onSettled = vi.fn();
    renderCard(makeProposal(), onSettled);

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(onSettled).toHaveBeenCalled());
  });

  test("rejecting sends the optional reason for this proposal, then reports it settled", async () => {
    const calls = stubFetch(() => reply(200, makeProposal({ status: "Rejected" })));
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
    stubFetch(() => reply(200, []));
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

  test.each<{
    status: LoopTemplateProposalStatus;
    edited: boolean;
    version: number | null;
    reason: string | null;
    shows: RegExp;
  }>([
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

describe("LoopTemplateProposalCard for an Edit List proposal", () => {
  const edits: LoopTemplateEdit[] = [
    {
      op: "editNodeField",
      nodeId: "n-build",
      field: "command",
      oldString: "make",
      newString: "make -j4",
    },
    { op: "setNodeField", nodeId: "n-gone", field: "command", value: "make check" },
    { op: "editFile", oldString: '"label":"Cleanup"', newString: '"label":"Tidy up"' },
  ];

  test("says how many edits there are beside the structural change list, with the edits collapsed", () => {
    const { container } = renderCard(makeProposal({ edits, document: undefined }));
    const text = container.textContent ?? "";

    expect(text).toContain("3 edits");
    expect(screen.getByRole("list", { name: "Proposed changes" })).toBeTruthy();
    const toggle = screen.getByRole("button", { name: "Show edits" });
    expect(toggle.getAttribute("aria-expanded")).toBe("false");
    expect(text).not.toContain("make -j4");
    expect(text).not.toContain("Tidy up");
  });

  test("Show edits lists each edit in order with what the agent sent, naming nodes by id while the document is not loaded", () => {
    renderCard(makeProposal({ edits, document: undefined }));

    fireEvent.click(screen.getByRole("button", { name: "Show edits" }));

    const rows = within(screen.getByRole("list", { name: "Edits" })).getAllByRole("listitem");
    expect(rows).toHaveLength(3);
    const [nodeEdit, fieldSet, fileEdit] = rows.map((row) => within(row));

    expect(nodeEdit.getByText("n-build")).toBeTruthy();
    expect(nodeEdit.getByText("command")).toBeTruthy();
    expect(nodeEdit.getByLabelText("Before").textContent).toBe("make");
    expect(nodeEdit.getByLabelText("After").textContent).toBe("make -j4");

    expect(fieldSet.getByText("n-gone")).toBeTruthy();
    expect(fieldSet.getByText("command")).toBeTruthy();
    expect(fieldSet.getByText(/replaced whole field/i)).toBeTruthy();
    expect(fieldSet.getByLabelText("Value").textContent).toBe("make check");
    expect(fieldSet.queryByLabelText("Before")).toBeNull();

    expect(fileEdit.getByText("Document JSON")).toBeTruthy();
    expect(fileEdit.getByLabelText("Before").textContent).toBe('"label":"Cleanup"');
    expect(fileEdit.getByLabelText("After").textContent).toBe('"label":"Tidy up"');
  });

  test("names a node by its label in the resulting document when that is loaded, and by its id when it is not in it", () => {
    renderCard(makeProposal({ edits, document: proposedDocument }));

    fireEvent.click(screen.getByRole("button", { name: "Show edits" }));

    const [nodeEdit, fieldSet] = within(screen.getByRole("list", { name: "Edits" }))
      .getAllByRole("listitem")
      .map((row) => within(row));
    expect(nodeEdit.getByText("Build")).toBeTruthy();
    expect(nodeEdit.queryByText("n-build")).toBeNull();
    expect(fieldSet.getByText("n-gone")).toBeTruthy();
  });

  test("a long whole-field value is collapsed until expanded", () => {
    const value = Array.from({ length: 40 }, (_, i) => `Step ${i + 1}: check the build.`).join(
      "\n",
    );
    renderCard(
      makeProposal({ edits: [{ op: "setNodeField", nodeId: "n-build", field: "prompt", value }] }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Show edits" }));

    expect(screen.getByLabelText("Value").textContent).toContain("Step 1: check the build.");
    expect(screen.getByLabelText("Value").textContent).not.toContain("Step 40");

    fireEvent.click(screen.getByRole("button", { name: "Show whole value" }));

    expect(screen.getByLabelText("Value").textContent).toBe(value);
  });

  test("a whole-document proposal shows no edit list", () => {
    renderCard(makeProposal({ edits: null }));

    expect(screen.queryByText(/^\d+ edits?$/)).toBeNull();
    expect(screen.queryByRole("button", { name: "Show edits" })).toBeNull();
    expect(screen.queryByRole("list", { name: "Edits" })).toBeNull();
  });
});
