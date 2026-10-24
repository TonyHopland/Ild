import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, cleanup, act, fireEvent, within } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import { NodeType, EdgeType, RecoveryPolicy, type LoopTemplateProposal } from "../../types";

// Every way the Loop Editor replaces its canvas with something else ends a
// proposal review that is open; only the open template's own replacement does.
// The banner's Discard sits with its decision actions, before Reject, and stays
// on a review whose proposal is no longer pending.
vi.mock("../../hooks/useSignalR", async () =>
  (await import("../../test-support")).fakeSignalRModule(),
);

import LoopEditor from "./index";
import { makeLoopTemplateProposal, reply, signalRHub, stubFetch } from "../../test-support";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";

const node = (id: string, type: NodeType, label: string) => ({ id, type, label, config: {} });
const edge = (id: string, sourceNodeId: string, targetNodeId: string) => ({
  id,
  sourceNodeId,
  targetNodeId,
  edgeType: EdgeType.OnSuccess,
});

const devLoop = {
  id: "tpl-1",
  name: "Dev Loop",
  description: "Standard development loop",
  version: 3,
  recoveryPolicy: RecoveryPolicy.AutoResume,
  nodes: [
    node("n-start", NodeType.Start, "Initialize"),
    node("n-cleanup", NodeType.Cleanup, "Tidy Up"),
  ],
  edges: [edge("e-1", "n-start", "n-cleanup")],
  createdAt: "2025-01-01T00:00:00Z",
  updatedAt: "2025-01-01T00:00:00Z",
  isArchived: false,
};

const docsLoop = {
  ...devLoop,
  id: "tpl-2",
  name: "Docs Loop",
  version: 1,
  nodes: [
    node("d-start", NodeType.Start, "Open Docs"),
    node("d-cleanup", NodeType.Cleanup, "Close Docs"),
  ],
  edges: [edge("d-1", "d-start", "d-cleanup")],
};

/** Version 2 of Dev Loop, as the version history opens it. */
const devLoopV2 = {
  ...devLoop,
  version: 2,
  nodes: [
    node("n-start", NodeType.Start, "Old Start"),
    node("n-cleanup", NodeType.Cleanup, "Tidy Up"),
  ],
};

const RATIONALE = "Lint before tidying up.";

const proposedDocument = {
  $schema: "ild-loop-template/v2",
  name: "Dev Loop",
  description: "Standard development loop",
  recoveryPolicy: RecoveryPolicy.AutoResume,
  nodes: [
    node("n-start", NodeType.Start, "Initialize"),
    { id: "n-lint", type: NodeType.Cmd, label: "Run Linter", config: { command: "make lint" } },
    node("n-cleanup", NodeType.Cleanup, "Tidy Up"),
  ],
  edges: [
    { ...edge("e-1", "n-start", "n-lint"), name: null },
    { ...edge("e-2", "n-lint", "n-cleanup"), name: null },
  ],
};

let proposal: LoopTemplateProposal;
let templates: (typeof devLoop)[];

beforeEach(() => {
  templates = [devLoop, docsLoop];
  proposal = makeLoopTemplateProposal({
    templateName: "Dev Loop",
    rationale: RATIONALE,
    createdByLoopRunId: null,
    createdByRunNodeId: null,
    createdByChatSessionId: "chat-9",
    chatReplySequence: 1,
    source: { kind: "chat", workItemId: null, chatSessionId: "chat-9", linkable: true },
    document: JSON.stringify(proposedDocument),
    changes: {
      nameChanged: false,
      descriptionChanged: false,
      recoveryPolicyChanged: false,
      nodesAdded: [{ id: "n-lint", label: "Run Linter", type: "Cmd" }],
      nodesRemoved: [],
      nodesChanged: [],
      edgesAdded: [],
      edgesRemoved: [],
    },
  });
  stubFetch(({ method, path }) => {
    if (method === "GET" && path === "/api/v1/looptemplates")
      return reply(
        200,
        templates.filter((t) => !t.isArchived),
      );
    if (method === "GET" && path === "/api/v1/looptemplates/tpl-1/versions")
      return reply(200, [
        {
          id: "v-3",
          loopTemplateId: "tpl-1",
          versionNumber: 3,
          createdAt: devLoop.createdAt,
          nodeCount: 2,
        },
        {
          id: "v-2",
          loopTemplateId: "tpl-1",
          versionNumber: 2,
          createdAt: devLoop.createdAt,
          nodeCount: 2,
        },
      ]);
    if (method === "GET" && path === "/api/v1/looptemplates/tpl-1/versions/2")
      return reply(200, devLoopV2);
    if (method === "POST" && path.endsWith("/archive")) {
      const id = path.split("/")[4];
      templates = templates.map((t) => (t.id === id ? { ...t, isArchived: true } : t));
      return reply(200);
    }
    if (method === "POST" && path === "/api/v1/looptemplates/validate")
      return reply(200, { valid: true, errors: [] });
    if (method === "GET" && path === "/api/v1/loop-template-proposals/lp-1")
      return reply(200, proposal);
    if (method === "GET" && path === "/api/v1/loop-template-proposals/pending-counts")
      return reply(200, {});
    if (method === "GET" && path === "/api/v1/looptemplates/node-outputs") return reply(200, {});
    if (method === "GET") return reply(200, []);
    return reply(404, { error: "not stubbed" });
  });
});

afterEach(() => {
  cleanup();
  signalRHub.reset();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

const authValue = {
  user: { id: "1", username: "test", createdAt: "" },
  token: "test-token",
  isAuthenticated: true,
  isLoading: false,
  login: vi.fn(),
  logout: vi.fn(),
};

/** Whether a node labelled `label` is on the canvas; an emptied canvas has none. */
function onCanvas(label: string): boolean {
  const canvas = document.querySelector<HTMLElement>(".react-flow");
  return canvas !== null && within(canvas).queryByText(label) !== null;
}
const discardButton = () => screen.queryByRole("button", { name: "Discard" });

/** The template list row of the template named `name`. */
function row(name: string): HTMLElement {
  const found = Array.from(document.querySelectorAll<HTMLElement>(".loop-list-item")).find((el) =>
    (el.querySelector(".loop-list-item-name")?.textContent ?? "").startsWith(name),
  );
  if (!found) throw new Error(`no template row named ${name}`);
  return found;
}

async function openReview() {
  render(
    <MemoryRouter initialEntries={["/loop-editor/tpl-1?proposal=lp-1"]}>
      <AuthContext.Provider value={authValue}>
        <Routes>
          <Route path="/loop-editor" element={<LoopEditor />} />
          <Route path="/loop-editor/:templateId" element={<LoopEditor />} />
        </Routes>
      </AuthContext.Provider>
    </MemoryRouter>,
    { wrapper: WorkItemHubProvider },
  );
  await waitFor(() => expect(onCanvas("Run Linter")).toBe(true));
  await waitFor(() => expect(discardButton()).not.toBeNull());
}

describe("replacing the canvas during a proposal review", () => {
  test("viewing an old version ends the review and shows that version", async () => {
    await openReview();

    fireEvent.click(within(row("Dev Loop")).getByText("Versions"));
    await screen.findByText("Version History");
    fireEvent.click(screen.getByText("v2"));

    await waitFor(() => expect(onCanvas("Old Start")).toBe(true));
    expect(onCanvas("Run Linter")).toBe(false);
    expect(discardButton()).toBeNull();
  });

  test("archiving the open template ends the review and empties the canvas", async () => {
    await openReview();

    fireEvent.click(within(row("Dev Loop")).getByText("Archive"));

    await waitFor(() => expect(discardButton()).toBeNull());
    await waitFor(() =>
      expect(screen.getByText("Select a template to view its graph")).toBeTruthy(),
    );
    expect(onCanvas("Run Linter")).toBe(false);
    expect(onCanvas("Initialize")).toBe(false);
  });

  test("archiving another template leaves the review open", async () => {
    await openReview();

    fireEvent.click(within(row("Docs Loop")).getByText("Archive"));

    await waitFor(() => expect(document.querySelectorAll(".loop-list-item").length).toBe(1));
    expect(discardButton()).not.toBeNull();
    expect(onCanvas("Run Linter")).toBe(true);
  });
});

describe("the review banner's Discard", () => {
  test("comes before Reject while pending, stays once the proposal is stale, and still goes back to the saved version", async () => {
    await openReview();
    const discard = screen.getByRole("button", { name: "Discard" });
    const reject = screen.getByRole("button", { name: "Reject" });
    expect(discard.compareDocumentPosition(reject) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

    proposal = { ...proposal, status: "Stale", latestVersion: 4 };
    act(() => {
      signalRHub.dispatch("LoopTemplateProposalsChanged", { templateId: "tpl-1" });
    });
    await waitFor(() => expect(screen.queryByRole("button", { name: "Reject" })).toBeNull());
    expect(discardButton()).not.toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Discard" }));
    await waitFor(() => expect(onCanvas("Run Linter")).toBe(false));
    expect(onCanvas("Initialize")).toBe(true);
    expect(discardButton()).toBeNull();
  });
});
