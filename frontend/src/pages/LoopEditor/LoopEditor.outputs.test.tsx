import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, cleanup, fireEvent, within, act } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import { EdgeType, NodeType, RecoveryPolicy } from "../../types";

// A node declares each of its outputs once, as an object in config.outputs. The
// editor lists the named ones, keeps whatever else an output object carries, and
// learns the fixed and reserved outputs of each node type from the server.

vi.mock("../../hooks/useSignalR", () => ({
  useSignalR: () => ({
    connectionState: "connected",
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn(() => Promise.resolve()),
  }),
}));

import LoopEditor from "./index";

const RESERVED = [
  "on_rejected",
  "on_merge_conflict",
  "on_ci_failed",
  "on_comment",
  "on_approved",
  "on_ci_passed",
  "on_merged",
  "on_abandoned",
];

const SUCCESS_AND_FAILURE = [{ name: "OnSuccess" }, { name: "OnFailure" }];

// GET /api/v1/looptemplates/node-outputs
const NODE_OUTPUTS = {
  Start: SUCCESS_AND_FAILURE,
  Cmd: SUCCESS_AND_FAILURE,
  AI: SUCCESS_AND_FAILURE,
  Human: SUCCESS_AND_FAILURE,
  Prompt: SUCCESS_AND_FAILURE,
  PR: [...SUCCESS_AND_FAILURE, ...RESERVED.map((name) => ({ name, reserved: true }))],
  Condition: [{ name: "OnFailure" }],
  Cleanup: [],
};

type Call = { method: string; path: string; body: unknown };

interface Server {
  template?: unknown;
  nodeOutputsFail?: boolean;
  upgradedDocument?: string;
}

function respond(status: number, json: unknown) {
  return Promise.resolve({
    ok: status < 400,
    status,
    statusText: status < 400 ? "OK" : "Error",
    text: () => Promise.resolve(json === undefined ? "" : JSON.stringify(json)),
  });
}

/** Stands in for the ILD API at the HTTP boundary and records every request. */
function serve(server: Server) {
  const calls: Call[] = [];
  const fetchMock = vi.fn((url: string, init?: RequestInit) => {
    const method = (init?.method ?? "GET").toUpperCase();
    const path = String(url).replace(/^.*\/api\/v1/, "");
    const body = typeof init?.body === "string" ? JSON.parse(init.body) : undefined;
    calls.push({ method, path, body });

    if (method === "GET" && path === "/looptemplates/node-outputs") {
      return server.nodeOutputsFail
        ? respond(500, { error: "unavailable" })
        : respond(200, NODE_OUTPUTS);
    }
    if (method === "GET" && /^\/looptemplates(\?|$)/.test(path)) {
      return respond(200, server.template ? [server.template] : []);
    }
    if (method === "POST" && path === "/looptemplates/validate") {
      return respond(200, { valid: true, errors: [] });
    }
    if (method === "POST" && path === "/looptemplates/upgrade-document") {
      return respond(200, { document: server.upgradedDocument });
    }
    if (method === "POST" && path === "/looptemplates") {
      return respond(200, { id: "tpl-new" });
    }
    if (method === "PUT" && path.startsWith("/looptemplates/")) {
      return respond(200, server.template);
    }
    return respond(200, null);
  });
  vi.stubGlobal("fetch", fetchMock);
  return calls;
}

const authValue = {
  user: { id: "1", username: "test", createdAt: "" },
  token: "test-token",
  isAuthenticated: true,
  isLoading: false,
  login: vi.fn(),
  logout: vi.fn(),
};

function renderEditor(path: string) {
  render(
    <MemoryRouter initialEntries={[path]}>
      <AuthContext.Provider value={authValue}>
        <Routes>
          <Route path="/loop-editor" element={<LoopEditor />} />
          <Route path="/loop-editor/:templateId" element={<LoopEditor />} />
        </Routes>
      </AuthContext.Provider>
    </MemoryRouter>,
  );
}

interface TemplateNode {
  id: string;
  type: NodeType;
  label: string;
  config: Record<string, unknown>;
}

interface TemplateEdge {
  id: string;
  sourceNodeId: string;
  targetNodeId: string;
  edgeType: EdgeType;
  name?: string | null;
}

/** Start → {node} → Cleanup, plus the node's own Custom edges into Cleanup. */
function templateWith(node: TemplateNode, customEdges: string[] = []) {
  const edges: TemplateEdge[] = [
    { id: "e-in", sourceNodeId: "n-start", targetNodeId: node.id, edgeType: EdgeType.OnSuccess },
    ...customEdges.map((name) => ({
      id: `e-${name}`,
      sourceNodeId: node.id,
      targetNodeId: "n-cleanup",
      edgeType: EdgeType.Custom,
      name,
    })),
  ];
  if (node.type !== NodeType.Condition) {
    edges.push({
      id: "e-out",
      sourceNodeId: node.id,
      targetNodeId: "n-cleanup",
      edgeType: EdgeType.OnSuccess,
    });
  }
  return {
    id: "tpl-1",
    name: "Dev Loop",
    description: "",
    version: 1,
    recoveryPolicy: RecoveryPolicy.AutoResume,
    nodes: [
      { id: "n-start", type: NodeType.Start, label: "Initialize", config: {} },
      node,
      { id: "n-cleanup", type: NodeType.Cleanup, label: "Tidy Up", config: {} },
    ],
    edges,
    createdAt: "2025-01-01T00:00:00Z",
    updatedAt: "2025-01-01T00:00:00Z",
    isArchived: false,
  };
}

async function openNode(server: Server, label: string) {
  const calls = serve(server);
  renderEditor("/loop-editor/tpl-1");
  await waitFor(() => expect(screen.getByText(label)).toBeTruthy());
  // Let every mount-time request settle so the node opens against its answers.
  await act(async () => {});
  await act(async () => {});
  fireEvent.click(screen.getByText(label));
  const dialog = await screen.findByRole("dialog", { name: "Node Settings" });
  return { calls, dialog };
}

/** Saves the node settings, then the loop, and returns what was sent to the server. */
async function saveLoop(dialog: HTMLElement, calls: Call[]) {
  fireEvent.click(within(dialog).getByText("Save"));
  await waitFor(() => expect(screen.queryByRole("dialog", { name: "Node Settings" })).toBeNull());
  fireEvent.click(screen.getByText("Save"));
  fireEvent.click(await screen.findByText("Save changes"));
  await waitFor(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
  const puts = calls.filter((c) => c.method === "PUT");
  return puts[puts.length - 1].body as {
    nodes: TemplateNode[];
    edges: TemplateEdge[];
  };
}

type Output = { name: string } & Record<string, unknown>;

function outputsOf(saved: { nodes: TemplateNode[] }, id: string): Output[] {
  return saved.nodes.find((n) => n.id === id)!.config.outputs as Output[];
}

function configOf(saved: { nodes: TemplateNode[] }, id: string) {
  return saved.nodes.find((n) => n.id === id)!.config;
}

/** The single text box in the Outputs list showing `name` (not a rule or case field). */
function outputField(dialog: HTMLElement, name: string, except: HTMLElement[] = []) {
  const fields = within(dialog)
    .queryAllByDisplayValue(name)
    .filter((el) => !except.includes(el));
  expect(fields).toHaveLength(1);
  return fields[0] as HTMLInputElement;
}

/** The remove button on the same row as `field`. */
function removeButtonFor(field: HTMLElement) {
  let row: HTMLElement | null = field.parentElement;
  for (let depth = 0; row && depth < 3; depth++, row = row.parentElement) {
    const buttons = within(row).queryAllByRole("button", { name: /remove/i });
    if (buttons.length === 1) return buttons[0];
  }
  throw new Error("no remove button on this output's row");
}

function isEditable(el: HTMLElement) {
  const input = el as HTMLInputElement;
  return !input.readOnly && !input.disabled;
}

function isListed(dialog: HTMLElement, name: string) {
  return (
    within(dialog).queryAllByDisplayValue(name).length +
      within(dialog).queryAllByText(name).length >
    0
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("Loop Editor — node outputs", () => {
  test("renaming an AI output keeps its other fields and renames its match rule and wired edge", async () => {
    const ai: TemplateNode = {
      id: "n-ai",
      type: NodeType.AI,
      label: "Reviewer",
      config: {
        prompt: "Review it",
        outputs: [
          { name: "OnSuccess" },
          { name: "OnFailure" },
          { name: "reject", visible: false, color: "x" },
        ],
        matchRules: [{ pattern: "REJECT", edgeName: "reject" }],
      },
    };
    const { calls, dialog } = await openNode(
      { template: templateWith(ai, ["reject"]) },
      "Reviewer",
    );

    // Success and failure are not named outputs, so they are not listed.
    expect(within(dialog).queryAllByDisplayValue("OnSuccess")).toHaveLength(0);
    expect(within(dialog).queryAllByDisplayValue("OnFailure")).toHaveLength(0);

    const ruleEdge = within(dialog).getByLabelText("Edge name 1");
    fireEvent.change(outputField(dialog, "reject", [ruleEdge]), { target: { value: "rework" } });

    const saved = await saveLoop(dialog, calls);
    const outputs = outputsOf(saved, "n-ai");
    expect(outputs.map((o) => o.name)).not.toContain("reject");
    expect(outputs.find((o) => o.name === "rework")).toEqual({
      name: "rework",
      visible: false,
      color: "x",
    });
    expect(configOf(saved, "n-ai").matchRules).toEqual([{ pattern: "REJECT", edgeName: "rework" }]);
    expect(configOf(saved, "n-ai")).not.toHaveProperty("customEdges");
    const wired = saved.edges.filter(
      (e) => e.sourceNodeId === "n-ai" && e.edgeType === EdgeType.Custom,
    );
    expect(wired.map((e) => e.name)).toEqual(["rework"]);
  });

  test("renaming a Condition output renames the case that routes to it and its wired edge", async () => {
    const gate: TemplateNode = {
      id: "n-gate",
      type: NodeType.Condition,
      label: "Gate",
      config: {
        cases: [{ variant: "PrExists", edgeName: "has-pr" }],
        defaultEdge: "otherwise",
        output: "{{Node.Input}}",
        outputs: [{ name: "OnFailure" }, { name: "has-pr", color: "x" }, { name: "otherwise" }],
      },
    };
    const { calls, dialog } = await openNode(
      { template: templateWith(gate, ["has-pr", "otherwise"]) },
      "Gate",
    );

    const caseEdge = within(dialog).getByLabelText("Case edge name 1");
    fireEvent.change(outputField(dialog, "has-pr", [caseEdge]), { target: { value: "pr-open" } });

    const saved = await saveLoop(dialog, calls);
    const outputs = outputsOf(saved, "n-gate");
    expect(outputs.map((o) => o.name)).not.toContain("has-pr");
    expect(outputs.find((o) => o.name === "pr-open")).toEqual({ name: "pr-open", color: "x" });
    const cases = configOf(saved, "n-gate").cases as Array<{ edgeName: string }>;
    expect(cases[0].edgeName).toBe("pr-open");
    const wired = saved.edges
      .filter((e) => e.sourceNodeId === "n-gate" && e.edgeType === EdgeType.Custom)
      .map((e) => e.name ?? "")
      .sort((a, b) => a.localeCompare(b));
    expect(wired).toEqual(["otherwise", "pr-open"]);
  });

  test.each([
    ["available", false],
    ["failing", true],
  ])(
    "a match rule naming an undeclared output declares it on save (node-outputs %s)",
    async (_, nodeOutputsFail) => {
      const ai: TemplateNode = {
        id: "n-ai",
        type: NodeType.AI,
        label: "Reviewer",
        config: { prompt: "Review it", outputs: [{ name: "approve", color: "green" }] },
      };
      const { calls, dialog } = await openNode(
        { template: templateWith(ai), nodeOutputsFail },
        "Reviewer",
      );

      fireEvent.click(within(dialog).getByText("+ Add rule"));
      fireEvent.change(within(dialog).getByLabelText("Match pattern 1"), {
        target: { value: "ESCALATE" },
      });
      fireEvent.change(within(dialog).getByLabelText("Edge name 1"), {
        target: { value: "escalate" },
      });

      const saved = await saveLoop(dialog, calls);
      const outputs = outputsOf(saved, "n-ai");
      expect(outputs).toContainEqual({ name: "approve", color: "green" });
      expect(outputs.filter((o) => o.name === "escalate")).toHaveLength(1);
      expect(configOf(saved, "n-ai").matchRules).toEqual([
        { pattern: "ESCALATE", edgeName: "escalate" },
      ]);
    },
  );

  test("adding and removing Human outputs edits the output objects and keeps the rest", async () => {
    const human: TemplateNode = {
      id: "n-human",
      type: NodeType.Human,
      label: "Sign Off",
      config: {
        prompt: "Ship it?",
        inputLabel: "Why?",
        outputs: [
          { name: "approve", visible: false },
          { name: "later", color: "x" },
        ],
      },
    };
    const { calls, dialog } = await openNode(
      { template: templateWith(human, ["approve"]) },
      "Sign Off",
    );

    fireEvent.click(removeButtonFor(outputField(dialog, "later")));

    const before = new Set(within(dialog).queryAllByRole("textbox"));
    fireEvent.click(within(dialog).getByRole("button", { name: /add (output|edge)/i }));
    const added = within(dialog)
      .queryAllByRole("textbox")
      .filter((el) => !before.has(el));
    expect(added).toHaveLength(1);
    fireEvent.change(added[0], { target: { value: "escalate" } });

    const saved = await saveLoop(dialog, calls);
    const outputs = outputsOf(saved, "n-human");
    expect(outputs).toContainEqual({ name: "approve", visible: false });
    expect(outputs.map((o) => o.name)).toContain("escalate");
    expect(outputs.map((o) => o.name)).not.toContain("later");
    expect(configOf(saved, "n-human")).not.toHaveProperty("customEdges");
  });

  test("a PR node lists every reserved output, none of which can be renamed or removed", async () => {
    const pr: TemplateNode = {
      id: "n-pr",
      type: NodeType.PR,
      label: "Pull Request",
      config: { prDescriptionTemplate: "t", outputs: [{ name: "deploy" }] },
    };
    const { dialog } = await openNode({ template: templateWith(pr, ["deploy"]) }, "Pull Request");

    await waitFor(() => expect(isListed(dialog, "on_merged")).toBe(true));
    for (const name of RESERVED) {
      expect(isListed(dialog, name)).toBe(true);
      for (const field of within(dialog).queryAllByDisplayValue(name)) {
        expect(isEditable(field)).toBe(false);
      }
    }
    expect(isEditable(outputField(dialog, "deploy"))).toBe(true);
    expect(isListed(dialog, "OnSuccess")).toBe(false);

    const removable = within(dialog)
      .queryAllByRole("button", { name: /remove/i })
      .filter((b) => !(b as HTMLButtonElement).disabled);
    expect(removable).toEqual([removeButtonFor(outputField(dialog, "deploy"))]);
  });
});

describe("Loop Editor — importing an older export", () => {
  const legacyExport = JSON.stringify({
    $schema: "ild-loop-template/v1",
    name: "Old Loop",
    description: "",
    recoveryPolicy: RecoveryPolicy.AutoResume,
    nodes: [
      { id: "s", type: NodeType.Start, label: "Start", config: {} },
      { id: "h", type: NodeType.Human, label: "Review", config: { customEdges: ["Respond"] } },
      { id: "c", type: NodeType.Cleanup, label: "Cleanup", config: {} },
    ],
    edges: [
      { id: "e1", sourceNodeId: "s", targetNodeId: "h", edgeType: EdgeType.OnSuccess, name: null },
      {
        id: "e2",
        sourceNodeId: "h",
        targetNodeId: "c",
        edgeType: EdgeType.Custom,
        name: "Respond",
      },
    ],
  });

  const upgradedHumanConfig = {
    outputs: [{ name: "Respond" }, { name: "OnSuccess" }, { name: "OnFailure" }],
  };
  const upgraded = JSON.stringify({
    ...JSON.parse(legacyExport),
    $schema: "ild-loop-template/v2",
    nodes: [
      { id: "s", type: NodeType.Start, label: "Start", config: { outputs: SUCCESS_AND_FAILURE } },
      { id: "h", type: NodeType.Human, label: "Review", config: upgradedHumanConfig },
      { id: "c", type: NodeType.Cleanup, label: "Cleanup", config: { outputs: [] } },
    ],
  });

  test("a v1 file is upgraded by the server and the upgraded loop is what gets created", async () => {
    const calls = serve({ upgradedDocument: upgraded });
    renderEditor("/loop-editor");
    await act(async () => {});

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    const file = new File([legacyExport], "old-loop.json", { type: "application/json" });
    fireEvent.change(input, { target: { files: [file] } });

    await waitFor(() =>
      expect(calls.some((c) => c.method === "POST" && c.path === "/looptemplates")).toBe(true),
    );
    const upgrade = calls.find(
      (c) => c.method === "POST" && c.path === "/looptemplates/upgrade-document",
    );
    expect(upgrade?.body).toEqual({ document: legacyExport });

    const created = calls.find((c) => c.method === "POST" && c.path === "/looptemplates")!.body as {
      name: string;
      nodes: TemplateNode[];
    };
    expect(created.name).toBe("Old Loop");
    expect(created.nodes.find((n) => n.id === "h")!.config).toEqual(upgradedHumanConfig);
  });
});
