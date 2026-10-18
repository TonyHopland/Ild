import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, cleanup, act, fireEvent, within } from "@testing-library/react";
import { MemoryRouter, Routes, Route, useNavigate, type NavigateFunction } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import { NodeType, EdgeType, RecoveryPolicy } from "../../types";
import { setCurrentChatSessionId } from "../../services/chatSessionStore";

// Reviewing a Loop Template Proposal in the Loop Editor. The editor talks to the
// real services; fetch answers as the server does, so the requests it sends —
// above all the save naming the proposal — are what is checked. The hub handlers
// are captured so a test can push a hint or a chat's loop edit.
vi.mock("../../hooks/useSignalR", async () =>
  (await import("../../test-support")).fakeSignalRModule(),
);

import LoopEditor from "./index";
import {
  makeLoopTemplateProposal,
  reply,
  signalRHub,
  stubFetch,
  type Call,
} from "../../test-support";
import type { LoopTemplateProposal } from "../../types";

const devLoop = {
  id: "tpl-1",
  name: "Dev Loop",
  description: "Standard development loop",
  version: 3,
  recoveryPolicy: RecoveryPolicy.AutoResume,
  nodes: [
    { id: "n-start", type: NodeType.Start, label: "Initialize", config: {} },
    { id: "n-cleanup", type: NodeType.Cleanup, label: "Tidy Up", config: {} },
  ],
  edges: [
    { id: "e-1", sourceNodeId: "n-start", targetNodeId: "n-cleanup", edgeType: EdgeType.OnSuccess },
  ],
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
    { id: "d-start", type: NodeType.Start, label: "Open Docs", config: {} },
    { id: "d-cleanup", type: NodeType.Cleanup, label: "Close Docs", config: {} },
  ],
  edges: [
    { id: "d-1", sourceNodeId: "d-start", targetNodeId: "d-cleanup", edgeType: EdgeType.OnSuccess },
  ],
};

/** The proposed whole document: a lint step between the two. */
const proposedDocument = {
  $schema: "ild-loop-template/v2",
  name: "Dev Loop",
  description: "Standard development loop",
  recoveryPolicy: RecoveryPolicy.AutoResume,
  nodes: [
    { id: "n-start", type: NodeType.Start, label: "Initialize", config: {} },
    { id: "n-lint", type: NodeType.Cmd, label: "Run Linter", config: { command: "make lint" } },
    { id: "n-cleanup", type: NodeType.Cleanup, label: "Tidy Up", config: {} },
  ],
  edges: [
    {
      id: "e-1",
      sourceNodeId: "n-start",
      targetNodeId: "n-lint",
      edgeType: EdgeType.OnSuccess,
      name: null,
    },
    {
      id: "e-2",
      sourceNodeId: "n-lint",
      targetNodeId: "n-cleanup",
      edgeType: EdgeType.OnSuccess,
      name: null,
    },
  ],
};

const RATIONALE = "Lint before tidying up.";

function proposalView(overrides: Partial<LoopTemplateProposal> = {}): LoopTemplateProposal {
  return makeLoopTemplateProposal({
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
      edgesAdded: [
        { sourceLabel: "Initialize", targetLabel: "Run Linter", edgeType: "OnSuccess", name: null },
        { sourceLabel: "Run Linter", targetLabel: "Tidy Up", edgeType: "OnSuccess", name: null },
      ],
      edgesRemoved: [
        { sourceLabel: "Initialize", targetLabel: "Tidy Up", edgeType: "OnSuccess", name: null },
      ],
    },
    ...overrides,
  });
}

/** What the fake server holds; a test changes it between steps. */
let server: {
  templates: (typeof devLoop)[];
  proposal: LoopTemplateProposal;
  /** What GET /loop-template-proposals lists, before its filters. */
  listed: LoopTemplateProposal[];
  counts: Record<string, number>;
};
let calls: Call[] = [];
/** How the stubbed fetch answers; a test may wrap it to hold one response. */
let answer: (call: Call) => Response | Promise<Response>;

/** The fake server's answer to `call`. */
function answerAsServer({ method, path, query, body }: Call): Response {
  if (method === "GET" && path === "/api/v1/looptemplates") return reply(200, server.templates);
  if (method === "GET" && path === "/api/v1/looptemplates/node-outputs") return reply(200, {});
  if (method === "POST" && path === "/api/v1/looptemplates/validate")
    return reply(200, { valid: true, errors: [] });
  if (method === "PUT" && path.startsWith("/api/v1/looptemplates/")) {
    const id = path.split("/").pop()!;
    const applied = body?.fromProposalId === "lp-1" && server.proposal.status === "Pending";
    server.templates = server.templates.map((t) =>
      t.id === id ? { ...t, ...(body as object), version: t.version + 1, id: t.id } : t,
    ) as (typeof devLoop)[];
    if (applied) server.proposal = { ...server.proposal, status: "Applied", appliedVersion: 4 };
    return reply(200, { id, version: 4, proposalApplied: applied });
  }
  if (method === "GET" && path === "/api/v1/loop-template-proposals/pending-counts")
    return reply(200, server.counts);
  if (method === "GET" && path === "/api/v1/loop-template-proposals/lp-1")
    return reply(200, server.proposal);
  if (method === "GET" && path === "/api/v1/loop-template-proposals") {
    const status = query.get("status");
    const templateId = query.get("templateId");
    return reply(
      200,
      server.listed.filter(
        (p) => (!status || p.status === status) && (!templateId || p.templateId === templateId),
      ),
    );
  }
  if (method === "POST" && path === "/api/v1/loop-template-proposals/lp-1/reject") {
    server.proposal = { ...server.proposal, status: "Rejected" };
    return reply(200, server.proposal);
  }
  if (method === "GET") return reply(200, []);
  return reply(404, { error: "not stubbed" });
}

beforeEach(() => {
  server = { templates: [devLoop], proposal: proposalView(), listed: [], counts: {} };
  answer = answerAsServer;
  calls = stubFetch((call) => answer(call));
});

afterEach(() => {
  cleanup();
  signalRHub.reset();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
  setCurrentChatSessionId(null);
});

const authValue = {
  user: { id: "1", username: "test", createdAt: "" },
  token: "test-token",
  isAuthenticated: true,
  isLoading: false,
  login: vi.fn(),
  logout: vi.fn(),
};

/** Moves the router the way a link elsewhere in the app does; set while an editor is rendered. */
let navigateTo: NavigateFunction | null = null;

function Navigator() {
  navigateTo = useNavigate();
  return null;
}

function renderEditor(path: string) {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Navigator />
      <AuthContext.Provider value={authValue}>
        <Routes>
          <Route path="/loop-editor" element={<LoopEditor />} />
          <Route path="/loop-editor/:templateId" element={<LoopEditor />} />
        </Routes>
      </AuthContext.Provider>
    </MemoryRouter>,
  );
}

function emit(event: string, payload: unknown) {
  act(() => signalRHub.dispatch(event, payload));
}

function pushLoopUpdate(chatSessionId: string, document: object) {
  emit("ChatLoopUpdate", { chatSessionId, document: JSON.stringify(document) });
}

const canvas = () => document.querySelector(".react-flow") as HTMLElement;

/** The node labels on the canvas. */
const onCanvas = (label: string) => within(canvas()).queryByText(label) !== null;

/** The review banner: the smallest element holding both its Discard button and the rationale. */
function banner(): HTMLElement | null {
  const discard = screen.queryByRole("button", { name: "Discard" });
  let el: HTMLElement | null = discard;
  while (el && !(el.textContent ?? "").includes(RATIONALE)) el = el.parentElement;
  if (el && el.contains(canvas())) return null;
  return el;
}

/** The template list row of the template named `name`. */
function row(name: string): HTMLElement {
  const found = Array.from(document.querySelectorAll<HTMLElement>(".loop-list-item")).find((el) =>
    (el.querySelector(".loop-list-item-name")?.textContent ?? "").startsWith(name),
  );
  if (!found) throw new Error(`no template row named ${name}`);
  return found;
}

function reviewControl(scope: HTMLElement = document.body): HTMLElement {
  return (
    within(scope).queryByRole("link", { name: /^review$/i }) ??
    within(scope).getByRole("button", { name: /^review$/i })
  );
}

async function openReview(path = "/loop-editor/tpl-1?proposal=lp-1") {
  renderEditor(path);
  await waitFor(() => expect(onCanvas("Run Linter")).toBe(true));
  await waitFor(() => expect(banner()).not.toBeNull());
}

async function saveThroughTheReviewGate() {
  fireEvent.click(screen.getByText("Save"));
  fireEvent.click(await screen.findByText("Save changes"));
  await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
  return calls.find((c) => c.method === "PUT")!;
}

const savedLabels = (put: Call) =>
  ((put.body?.nodes ?? []) as Array<{ label: string }>).map((n) => n.label).sort();

describe("reviewing a loop template proposal in the Loop Editor", () => {
  test("opens the proposal as the unsaved working copy under a banner, and saving names the proposal and closes the banner", async () => {
    await openReview();

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(onCanvas("Initialize")).toBe(true);
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
    const shown = banner()!;
    expect(shown.textContent).toContain("Run Linter");
    expect(
      Array.from(shown.querySelectorAll("a")).some((a) =>
        (a.getAttribute("href") ?? "").includes("chat-9"),
      ),
    ).toBe(true);

    const put = await saveThroughTheReviewGate();

    expect(put.path).toBe("/api/v1/looptemplates/tpl-1");
    expect(put.body?.fromProposalId).toBe("lp-1");
    expect(savedLabels(put)).toEqual(["Initialize", "Run Linter", "Tidy Up"]);
    await waitFor(() => expect(screen.queryByRole("button", { name: "Discard" })).toBeNull());
  });

  test("Discard goes back to the saved version and leaves the proposal pending", async () => {
    await openReview();

    fireEvent.click(screen.getByRole("button", { name: "Discard" }));

    await waitFor(() => expect(onCanvas("Run Linter")).toBe(false));
    expect(onCanvas("Initialize")).toBe(true);
    expect(onCanvas("Tidy Up")).toBe(true);
    expect(screen.queryByRole("button", { name: "Discard" })).toBeNull();
    expect(
      calls.filter((c) => c.method !== "GET" && c.path.includes("loop-template-proposals")),
    ).toEqual([]);
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });

  test("Reject on the banner rejects the proposal and goes back to the saved version", async () => {
    await openReview();

    fireEvent.click(within(banner()!).getByRole("button", { name: "Reject" }));
    const confirm = screen.queryByRole("button", { name: /confirm/i });
    if (confirm) fireEvent.click(confirm);

    await waitFor(() =>
      expect(
        calls.some(
          (c) => c.method === "POST" && c.path === "/api/v1/loop-template-proposals/lp-1/reject",
        ),
      ).toBe(true),
    );
    await waitFor(() => expect(onCanvas("Run Linter")).toBe(false));
    expect(onCanvas("Initialize")).toBe(true);
    expect(screen.queryByRole("button", { name: "Discard" })).toBeNull();
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });

  test("Review over unsaved changes asks first: Cancel keeps them and loads nothing, confirming loads the proposal; a clean canvas does not ask", async () => {
    server.listed = [proposalView()];
    setCurrentChatSessionId("s1");
    renderEditor("/loop-editor/tpl-1");
    await waitFor(() => expect(onCanvas("Initialize")).toBe(true));
    await screen.findByText(RATIONALE);

    fireEvent.click(reviewControl());
    await waitFor(() => expect(onCanvas("Run Linter")).toBe(true));
    expect(screen.queryByRole("dialog")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Discard" }));
    await waitFor(() => expect(onCanvas("Run Linter")).toBe(false));

    pushLoopUpdate("s1", {
      ...proposedDocument,
      nodes: [
        { id: "n-start", type: NodeType.Start, label: "Boot Up", config: {} },
        { id: "n-cleanup", type: NodeType.Cleanup, label: "Wind Down", config: {} },
      ],
      edges: [
        {
          id: "e-1",
          sourceNodeId: "n-start",
          targetNodeId: "n-cleanup",
          edgeType: EdgeType.OnSuccess,
          name: null,
        },
      ],
    });
    await waitFor(() => expect(onCanvas("Boot Up")).toBe(true));

    fireEvent.click(reviewControl());
    const ask = await screen.findByRole("dialog");
    fireEvent.click(within(ask).getByRole("button", { name: "Cancel" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(onCanvas("Boot Up")).toBe(true);
    expect(onCanvas("Run Linter")).toBe(false);
    expect(screen.queryByRole("button", { name: "Discard" })).toBeNull();

    fireEvent.click(reviewControl());
    const again = await screen.findByRole("dialog");
    const replace = within(again)
      .getAllByRole("button")
      .find((b) => b.textContent !== "Cancel")!;
    fireEvent.click(replace);

    await waitFor(() => expect(onCanvas("Run Linter")).toBe(true));
    expect(onCanvas("Boot Up")).toBe(false);
    await waitFor(() => expect(banner()).not.toBeNull());
  });

  test("a proposal going stale during review says so on the banner, offers no Reject, and Save still saves", async () => {
    await openReview();

    server.proposal = { ...server.proposal, status: "Stale", latestVersion: 4 };
    emit("LoopTemplateProposalsChanged", { templateId: "tpl-1" });

    await waitFor(() => expect(banner()?.textContent ?? "").toMatch(/stale/i));
    expect(within(banner()!).queryByRole("button", { name: "Reject" })).toBeNull();
    expect(onCanvas("Run Linter")).toBe(true);

    const put = await saveThroughTheReviewGate();
    expect(put.path).toBe("/api/v1/looptemplates/tpl-1");
    expect(savedLabels(put)).toEqual(["Initialize", "Run Linter", "Tidy Up"]);
  });

  test("a chat's loop edit changes the review copy, the banner stays, and the save still names the proposal", async () => {
    setCurrentChatSessionId("s1");
    await openReview();

    pushLoopUpdate("s1", {
      ...proposedDocument,
      nodes: proposedDocument.nodes.map((n) =>
        n.id === "n-lint" ? { ...n, label: "Run Strict Linter" } : n,
      ),
    });

    await waitFor(() => expect(onCanvas("Run Strict Linter")).toBe(true));
    expect(banner()).not.toBeNull();
    const put = await saveThroughTheReviewGate();
    expect(put.body?.fromProposalId).toBe("lp-1");
    expect(savedLabels(put)).toEqual(["Initialize", "Run Strict Linter", "Tidy Up"]);
  });

  test.each(["switching template", "New Template"])("%s closes the review", async (action) => {
    server.templates = [devLoop, docsLoop];
    await openReview();

    if (action === "New Template") fireEvent.click(screen.getByText("New Template"));
    else fireEvent.click(row("Docs Loop"));

    await waitFor(() => expect(screen.queryByRole("button", { name: "Discard" })).toBeNull());
    fireEvent.click(row("Dev Loop"));
    await waitFor(() => expect(onCanvas("Initialize")).toBe(true));
    expect(onCanvas("Run Linter")).toBe(false);
    expect(screen.queryByRole("button", { name: "Discard" })).toBeNull();
  });
});

describe("pending proposals in the template list", () => {
  test("a template shows a live count of its pending proposals, and the open one lists them with their source", async () => {
    server.templates = [devLoop, docsLoop];
    server.counts = { "tpl-1": 5, "tpl-2": 0 };
    server.listed = [
      proposalView(),
      proposalView({
        id: "lp-2",
        rationale: "An older idea from a deleted run.",
        createdByChatSessionId: null,
        createdByLoopRunId: "run-gone",
        source: { kind: "deleted", workItemId: null, chatSessionId: null, linkable: false },
      }),
    ];
    renderEditor("/loop-editor/tpl-1");

    await waitFor(() => expect(row("Dev Loop").textContent).toContain("5"));
    expect(row("Docs Loop").textContent).not.toContain("0");
    await screen.findByText(RATIONALE);
    await screen.findByText("An older idea from a deleted run.");
    expect(screen.getByText(/source deleted/i)).toBeTruthy();
    expect(
      Array.from(document.querySelectorAll("a")).some((a) =>
        (a.getAttribute("href") ?? "").includes("chat-9"),
      ),
    ).toBe(true);

    server.counts = { "tpl-1": 4 };
    emit("LoopTemplateProposalsChanged", { templateId: "tpl-1" });

    await waitFor(() => expect(row("Dev Loop").textContent).toContain("4"));
    expect(row("Dev Loop").textContent).not.toContain("5");
  });
});

describe("saving a listed proposal", () => {
  test("the open template then shows the version the proposal became, so a later save builds on it", async () => {
    server.listed = [proposalView()];
    answer = (call) => {
      if (call.method === "POST" && call.path.endsWith("/lp-1/save")) {
        server.templates = [
          {
            ...devLoop,
            version: 4,
            nodes: proposedDocument.nodes,
            edges: proposedDocument.edges,
          },
        ] as (typeof devLoop)[];
        server.listed = [];
        server.proposal = { ...server.proposal, status: "Applied", appliedVersion: 4 };
        return reply(200, { proposal: server.proposal, version: 4 });
      }
      return answerAsServer(call);
    };
    renderEditor("/loop-editor/tpl-1");
    await waitFor(() => expect(onCanvas("Initialize")).toBe(true));
    const listed = (await screen.findByText(RATIONALE)).closest(
      '[data-testid="proposal-card"]',
    ) as HTMLElement;

    fireEvent.click(within(listed).getByRole("button", { name: "Save" }));

    await waitFor(() => expect(onCanvas("Run Linter")).toBe(true));
    expect(screen.queryByRole("button", { name: "Discard" })).toBeNull();
  });
});

describe("leaving a review while its proposal is re-read", () => {
  test.each(["Discard", "Reject"])(
    "%s goes back to the saved version even when a hint re-reads the proposal meanwhile",
    async (action) => {
      await openReview();
      let held = false;
      let release: () => void = () => {};
      const templatesRead = new Promise<void>((resolve) => (release = resolve));
      answer = async (call) => {
        if (!held && call.method === "GET" && call.path === "/api/v1/looptemplates") {
          held = true;
          await templatesRead;
        }
        return answerAsServer(call);
      };

      fireEvent.click(within(banner()!).getByRole("button", { name: action }));
      const confirm = screen.queryByRole("button", { name: /confirm/i });
      if (confirm) fireEvent.click(confirm);
      await waitFor(() => expect(held).toBe(true));

      if (action === "Discard") server.proposal = { ...server.proposal, status: "Stale" };
      emit("LoopTemplateProposalsChanged", { templateId: "tpl-1" });
      await waitFor(() => expect(banner()?.textContent ?? "").toMatch(/no longer pending|stale/i));
      await act(async () => release());

      await waitFor(() => expect(onCanvas("Run Linter")).toBe(false));
      expect(onCanvas("Initialize")).toBe(true);
      expect(screen.queryByRole("button", { name: "Discard" })).toBeNull();
    },
  );
});

describe("a review opened while a save is in flight", () => {
  test("stays open when that save, made from no review, finishes", async () => {
    server.listed = [proposalView()];
    renderEditor("/loop-editor/tpl-1");
    await waitFor(() => expect(onCanvas("Initialize")).toBe(true));
    let held = false;
    let release: () => void = () => {};
    const saveAnswered = new Promise<void>((resolve) => (release = resolve));
    answer = async (call) => {
      if (call.method === "PUT") {
        held = true;
        await saveAnswered;
      }
      return answerAsServer(call);
    };
    fireEvent.click(screen.getByText("Save"));
    fireEvent.click(await screen.findByText("Save changes"));
    await waitFor(() => expect(held).toBe(true));

    const listed = (await screen.findByText(RATIONALE)).closest(
      '[data-testid="proposal-card"]',
    ) as HTMLElement;
    fireEvent.click(reviewControl(listed));
    await waitFor(() => expect(banner()).not.toBeNull());
    await act(async () => release());

    await screen.findByText("Saved!");
    expect(calls.find((c) => c.method === "PUT")?.body?.fromProposalId ?? null).toBeNull();
    expect(banner()).not.toBeNull();
    expect(onCanvas("Run Linter")).toBe(true);
  });
});

describe("a Review link to the template that is still open", () => {
  test("opens the review after the route has opened its template again, whichever effect runs first", async () => {
    renderEditor("/loop-editor/tpl-1");
    await waitFor(() => expect(onCanvas("Initialize")).toBe(true));
    await act(async () => {
      await navigateTo?.("/loop-editor");
    });
    expect(onCanvas("Initialize")).toBe(true);

    await act(async () => {
      await navigateTo?.("/loop-editor/tpl-1?proposal=lp-1");
    });

    await waitFor(() => expect(banner()).not.toBeNull());
    expect(onCanvas("Run Linter")).toBe(true);
    expect(calls.filter((c) => c.path === "/api/v1/loop-template-proposals/lp-1")).toHaveLength(1);
  });
});
