import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import ProposalReviewBanner from "./ProposalReviewBanner";
import { makeLoopTemplateProposal } from "../../../test-support";
import type { LoopTemplateProposal } from "../../../types";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function renderBanner(proposal: LoopTemplateProposal) {
  return render(
    <MemoryRouter>
      <ProposalReviewBanner
        proposal={proposal}
        onDiscard={vi.fn()}
        onRejected={vi.fn()}
        onRefused={vi.fn()}
      />
    </MemoryRouter>,
  );
}

describe("ProposalReviewBanner", () => {
  test("an Edit List proposal under review shows its edits, naming nodes from the document it produced", () => {
    renderBanner(
      makeLoopTemplateProposal({
        document: JSON.stringify({
          $schema: "ild-loop-template/v2",
          nodes: [{ id: "n-build", type: "Cmd", label: "Build", config: { command: "make -j4" } }],
          edges: [],
        }),
        edits: [
          {
            op: "editNodeField",
            nodeId: "n-build",
            field: "command",
            oldString: "make",
            newString: "make -j4",
          },
          { op: "editFile", oldString: '"label":"Cleanup"', newString: '"label":"Tidy up"' },
        ],
      }),
    );

    expect(screen.getByRole("region", { name: "Proposal under review" }).textContent).toContain(
      "2 edits",
    );
    fireEvent.click(screen.getByRole("button", { name: "Show edits" }));

    const [nodeEdit, fileEdit] = within(screen.getByRole("list", { name: "Edits" }))
      .getAllByRole("listitem")
      .map((row) => within(row));
    expect(nodeEdit.getByText("Build")).toBeTruthy();
    expect(nodeEdit.getByLabelText("After").textContent).toBe("make -j4");
    expect(fileEdit.getByText("Document JSON")).toBeTruthy();
  });

  test("a whole-document proposal under review shows no edit list", () => {
    renderBanner(makeLoopTemplateProposal({ edits: null }));

    expect(screen.queryByRole("button", { name: "Show edits" })).toBeNull();
  });
});
