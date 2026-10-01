import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, cleanup, fireEvent, within, act } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import { EdgeType, NodeType, RecoveryPolicy } from "../../types";

// A node declares each of its outputs once, as an object in config.outputs. The
// editor lists a Human or PR node's outputs, derives an AI or Condition node's
// from what routes to them, keeps whatever else an output object carries, and
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

/**
 * Start → {node} → Cleanup on success, plus the node's own Custom edges into
 * Cleanup, and its failure edge too when `failureWired`.
 */
function templateWith(
  node: TemplateNode,
  wiredNames: string[] = [],
  { failureWired = false }: { failureWired?: boolean } = {},
) {
  const edges: TemplateEdge[] = [
    { id: "e-in", sourceNodeId: "n-start", targetNodeId: node.id, edgeType: EdgeType.OnSuccess },
    ...wiredNames.map((name) => ({
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
  if (failureWired) {
    edges.push({
      id: "e-fail",
      sourceNodeId: node.id,
      targetNodeId: "n-cleanup",
      edgeType: EdgeType.OnFailure,
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
  return saveTemplate(calls);
}

/** Once the node settings have closed, saves the loop and returns what was sent. */
async function saveTemplate(calls: Call[]) {
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

/** The eye button of the output called exactly `name`, or null when it has none. */
function queryToggle(dialog: HTMLElement, name: string) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const label = new RegExp(
    `^(Visible to|Hidden from) user: ${escaped}( \\(no edge connected\\))?$`,
  );
  const found = within(dialog).queryAllByRole("button", { name: label });
  expect(found.length).toBeLessThanOrEqual(1);
  return (found[0] as HTMLButtonElement | undefined) ?? null;
}

function toggle(dialog: HTMLElement, name: string) {
  const found = queryToggle(dialog, name);
  expect(found, name).not.toBeNull();
  return found!;
}

function toggles(dialog: HTMLElement) {
  return within(dialog).queryAllByRole("button", {
    name: /^(Visible to|Hidden from) user: /,
  }) as HTMLButtonElement[];
}

function isOn(button: HTMLElement) {
  return button.getAttribute("aria-pressed") === "true";
}

describe("Loop Editor — node outputs", () => {
  const reviewer = (): TemplateNode => ({
    id: "n-ai",
    type: NodeType.AI,
    label: "Reviewer",
    config: {
      prompt: "Review it",
      matchRules: [{ pattern: "REJECT", edgeName: "reject" }],
      outputs: [
        { name: "OnSuccess" },
        { name: "OnFailure" },
        { name: "reject", color: "red", visible: false },
        { name: "spare" },
      ],
    },
  });

  const gate = (): TemplateNode => ({
    id: "n-gate",
    type: NodeType.Condition,
    label: "Gate",
    config: {
      cases: [
        { variant: "PrExists", edgeName: "has-pr" },
        { variant: "HasTag", tag: "urgent", edgeName: "otherwise" },
      ],
      defaultEdge: "otherwise",
      output: "{{Node.Input}}",
      outputs: [
        { name: "OnFailure" },
        { name: "has-pr", color: "x" },
        { name: "otherwise" },
        { name: "stale" },
      ],
    },
  });

  function wiredOutputNames(saved: { edges: TemplateEdge[] }, source: string) {
    return saved.edges
      .filter((e) => e.sourceNodeId === source && e.edgeType === EdgeType.Custom)
      .map((e) => e.name);
  }

  /** Clicks Save and returns the confirmation that saving would remove edges. */
  async function saveAskingToRemoveEdges(dialog: HTMLElement) {
    fireEvent.click(within(dialog).getByRole("button", { name: "Save" }));
    return await screen.findByRole("dialog", { name: "Remove edges" });
  }

  test.each([
    ["an AI node", reviewer, "Reviewer"],
    ["a Condition node", gate, "Gate"],
  ])("%s has no Outputs list and no eye", async (_, opened, label) => {
    const node = opened();
    const { dialog } = await openNode({ template: templateWith(node) }, label);

    expect(within(dialog).queryByText("Outputs")).toBeNull();
    expect(within(dialog).queryByText("+ Add output")).toBeNull();
    expect(within(dialog).queryAllByDisplayValue("spare")).toHaveLength(0);
    expect(within(dialog).queryAllByDisplayValue("stale")).toHaveLength(0);
    expect(within(dialog).queryAllByDisplayValue("OnSuccess")).toHaveLength(0);
    expect(toggles(dialog)).toHaveLength(0);
  });

  test("saving an AI node declares what its rules route to and keeps those outputs' fields", async () => {
    const { calls, dialog } = await openNode(
      { template: templateWith(reviewer(), ["reject"]) },
      "Reviewer",
    );

    fireEvent.click(within(dialog).getByText("+ Add rule"));
    fireEvent.change(within(dialog).getByLabelText("Match pattern 2"), {
      target: { value: "ESCALATE" },
    });
    fireEvent.change(within(dialog).getByLabelText("Output name 2"), {
      target: { value: " escalate " },
    });

    // The unwired output nothing routes to is dropped without asking.
    const saved = await saveLoop(dialog, calls);
    expect(outputsOf(saved, "n-ai")).toEqual([
      { name: "OnSuccess" },
      { name: "OnFailure" },
      { name: "reject", color: "red", visible: false },
      { name: "escalate" },
    ]);
    expect(configOf(saved, "n-ai").matchRules).toEqual([
      { pattern: "REJECT", edgeName: "reject" },
      { pattern: "ESCALATE", edgeName: "escalate" },
    ]);
    expect(configOf(saved, "n-ai")).not.toHaveProperty("customEdges");
    expect(wiredOutputNames(saved, "n-ai")).toEqual(["reject"]);
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
        config: {
          prompt: "Review it",
          matchRules: [{ pattern: "APPROVE", edgeName: "approve" }],
          outputs: [{ name: "approve", color: "green" }],
        },
      };
      const { calls, dialog } = await openNode(
        { template: templateWith(ai), nodeOutputsFail },
        "Reviewer",
      );

      fireEvent.click(within(dialog).getByText("+ Add rule"));
      fireEvent.change(within(dialog).getByLabelText("Match pattern 2"), {
        target: { value: "ESCALATE" },
      });
      fireEvent.change(within(dialog).getByLabelText("Output name 2"), {
        target: { value: "escalate" },
      });

      const saved = await saveLoop(dialog, calls);
      const outputs = outputsOf(saved, "n-ai");
      expect(outputs).toContainEqual({ name: "approve", color: "green" });
      expect(outputs.filter((o) => o.name === "escalate")).toHaveLength(1);
      expect(configOf(saved, "n-ai").matchRules).toEqual([
        { pattern: "APPROVE", edgeName: "approve" },
        { pattern: "ESCALATE", edgeName: "escalate" },
      ]);
    },
  );

  test("removing the last rule routing to a wired output asks before saving, and cancelling saves nothing", async () => {
    const { calls, dialog } = await openNode(
      { template: templateWith(reviewer(), ["reject"]) },
      "Reviewer",
    );

    fireEvent.click(within(dialog).getByLabelText("Remove rule 1"));
    const confirm = await saveAskingToRemoveEdges(dialog);
    expect(within(confirm).getByText("The edge 'reject' to Tidy Up")).toBeTruthy();

    fireEvent.click(within(confirm).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("dialog", { name: "Remove edges" })).toBeNull();
    expect(screen.getByRole("dialog", { name: "Node Settings" })).toBeTruthy();
    expect(calls.some((c) => c.method === "PUT")).toBe(false);
  });

  test("confirming saves the AI node without the output and its edge", async () => {
    const { calls, dialog } = await openNode(
      { template: templateWith(reviewer(), ["reject"]) },
      "Reviewer",
    );

    fireEvent.change(within(dialog).getByLabelText("Output name 1"), {
      target: { value: "rework" },
    });
    const confirm = await saveAskingToRemoveEdges(dialog);
    fireEvent.click(within(confirm).getByRole("button", { name: "Save and remove" }));

    const saved = await saveTemplate(calls);
    expect(outputsOf(saved, "n-ai")).toEqual([
      { name: "OnSuccess" },
      { name: "OnFailure" },
      { name: "rework" },
    ]);
    expect(configOf(saved, "n-ai").matchRules).toEqual([{ pattern: "REJECT", edgeName: "rework" }]);
    expect(wiredOutputNames(saved, "n-ai")).toEqual([]);
  });

  test("saving a Condition node unchanged keeps what its cases and default route to, and nothing else", async () => {
    const { calls, dialog } = await openNode(
      { template: templateWith(gate(), ["has-pr", "otherwise"]) },
      "Gate",
    );

    const saved = await saveLoop(dialog, calls);
    expect(outputsOf(saved, "n-gate")).toEqual([
      { name: "OnFailure" },
      { name: "has-pr", color: "x" },
      { name: "otherwise" },
    ]);
    expect(wiredOutputNames(saved, "n-gate")).toEqual(["has-pr", "otherwise"]);
  });

  test("moving a Condition's case and default off a wired output asks before its edge goes", async () => {
    const { calls, dialog } = await openNode(
      { template: templateWith(gate(), ["has-pr", "otherwise"]) },
      "Gate",
    );

    fireEvent.click(within(dialog).getByLabelText("Remove case 2"));
    fireEvent.change(within(dialog).getByLabelText("Default output"), {
      target: { value: "has-pr" },
    });
    const confirm = await saveAskingToRemoveEdges(dialog);
    expect(within(confirm).getByText("The edge 'otherwise' to Tidy Up")).toBeTruthy();
    fireEvent.click(within(confirm).getByRole("button", { name: "Save and remove" }));

    const saved = await saveTemplate(calls);
    const config = configOf(saved, "n-gate");
    expect(outputsOf(saved, "n-gate")).toEqual([
      { name: "OnFailure" },
      { name: "has-pr", color: "x" },
    ]);
    expect(config.cases).toEqual([{ variant: "PrExists", edgeName: "has-pr" }]);
    expect(config.defaultEdge).toBe("has-pr");
    expect(wiredOutputNames(saved, "n-gate")).toEqual(["has-pr"]);
  });

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
    fireEvent.click(within(dialog).getByRole("button", { name: /add output/i }));
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

  test("a PR node lists success, failure and every reserved output, none of which can be renamed or removed", async () => {
    const pr: TemplateNode = {
      id: "n-pr",
      type: NodeType.PR,
      label: "Pull Request",
      config: { prDescriptionTemplate: "t", outputs: [{ name: "deploy" }] },
    };
    const { dialog } = await openNode({ template: templateWith(pr, ["deploy"]) }, "Pull Request");

    await waitFor(() => expect(isListed(dialog, "on_merged")).toBe(true));
    for (const name of ["OnSuccess", "OnFailure", ...RESERVED]) {
      const fields = within(dialog).queryAllByDisplayValue(name);
      expect(fields, name).toHaveLength(1);
      expect(isEditable(fields[0]), name).toBe(false);
    }
    expect(isEditable(outputField(dialog, "deploy"))).toBe(true);

    const removable = within(dialog)
      .queryAllByRole("button", { name: /remove/i })
      .filter((b) => !(b as HTMLButtonElement).disabled);
    expect(removable).toEqual([removeButtonFor(outputField(dialog, "deploy"))]);
  });

  test("saving a PR node drops the PR comment template it no longer shows", async () => {
    const pr: TemplateNode = {
      id: "n-pr",
      type: NodeType.PR,
      label: "Pull Request",
      config: { prDescriptionTemplate: "t", prCommentTemplate: "Update", outputs: [] },
    };
    const { calls, dialog } = await openNode({ template: templateWith(pr) }, "Pull Request");
    expect(within(dialog).queryByText(/PR Comment Template/)).toBeNull();

    const saved = await saveLoop(dialog, calls);
    expect(configOf(saved, "n-pr")).not.toHaveProperty("prCommentTemplate");
    expect(configOf(saved, "n-pr").prDescriptionTemplate).toBe("t");
  });

  test("the header names the node's type and there is no Type field", async () => {
    const human: TemplateNode = {
      id: "n-human",
      type: NodeType.Human,
      label: "Sign Off",
      config: { outputs: [] },
    };
    const { dialog } = await openNode({ template: templateWith(human) }, "Sign Off");

    expect(within(dialog).getByRole("heading", { level: 2 }).textContent).toBe("👤Human");
    expect(within(dialog).queryByText("Type")).toBeNull();
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

describe("Loop Editor — deleting and naming outputs", () => {
  const signOff = (): TemplateNode => ({
    id: "n-human",
    type: NodeType.Human,
    label: "Sign Off",
    config: {
      prompt: "Ship it?",
      outputs: [
        { name: "OnSuccess" },
        { name: "OnFailure" },
        { name: "approve" },
        { name: "later", color: "red" },
        { name: "spare", visible: false },
      ],
    },
  });

  const reviewer = (): TemplateNode => ({
    id: "n-ai",
    type: NodeType.AI,
    label: "Reviewer",
    config: {
      prompt: "Review it",
      matchRules: [{ pattern: "REJECT", edgeName: "reject" }],
      outputs: [{ name: "OnSuccess" }, { name: "OnFailure" }, { name: "reject" }],
    },
  });

  const gate = (config: Record<string, unknown> = {}): TemplateNode => ({
    id: "n-gate",
    type: NodeType.Condition,
    label: "Gate",
    config: {
      cases: [{ variant: "PrExists", edgeName: "has-pr" }],
      defaultEdge: "otherwise",
      output: "{{Node.Input}}",
      outputs: [{ name: "OnFailure" }, { name: "has-pr" }, { name: "otherwise" }],
      ...config,
    },
  });

  function wiredOutputNames(saved: { edges: TemplateEdge[] }, source: string) {
    return saved.edges
      .filter((e) => e.sourceNodeId === source && e.edgeType === EdgeType.Custom)
      .map((e) => e.name);
  }

  function saveButton(dialog: HTMLElement) {
    return within(dialog).getByRole("button", { name: "Save" });
  }

  /** Save is shown as unavailable, and clicking it anyway keeps the settings open. */
  function expectSaveRefused(dialog: HTMLElement) {
    expect(saveButton(dialog).getAttribute("aria-disabled")).toBe("true");
    fireEvent.click(saveButton(dialog));
    expect(screen.getByRole("dialog", { name: "Node Settings" })).toBeTruthy();
  }

  function expectSaveAvailable(dialog: HTMLElement) {
    expect(saveButton(dialog).getAttribute("aria-disabled")).toBe("false");
  }

  async function confirmDelete(dialog: HTMLElement, field: HTMLElement) {
    fireEvent.click(removeButtonFor(field));
    const confirm = await screen.findByRole("dialog", { name: "Delete output" });
    fireEvent.click(within(confirm).getByRole("button", { name: "Delete output" }));
    expect(screen.queryByRole("dialog", { name: "Delete output" })).toBeNull();
    return dialog;
  }

  test("deleting a wired output asks first, and cancelling changes nothing", async () => {
    const { calls, dialog } = await openNode(
      { template: templateWith(signOff(), ["later"]) },
      "Sign Off",
    );

    fireEvent.click(removeButtonFor(outputField(dialog, "later")));

    const confirm = await screen.findByRole("dialog", { name: "Delete output" });
    expect(within(confirm).getByText("The edge to Tidy Up")).toBeTruthy();
    fireEvent.click(within(confirm).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("dialog", { name: "Delete output" })).toBeNull();
    expect(outputField(dialog, "later")).toBeTruthy();

    const saved = await saveLoop(dialog, calls);
    expect(outputsOf(saved, "n-human")).toContainEqual({ name: "later", color: "red" });
    expect(wiredOutputNames(saved, "n-human")).toEqual(["later"]);
  });

  test("confirming the delete removes the output and its wired edge", async () => {
    const { calls, dialog } = await openNode(
      { template: templateWith(signOff(), ["later"]) },
      "Sign Off",
    );

    await confirmDelete(dialog, outputField(dialog, "later"));

    expect(within(dialog).queryAllByDisplayValue("later")).toHaveLength(0);
    const saved = await saveLoop(dialog, calls);
    const outputs = outputsOf(saved, "n-human");
    expect(outputs.map((o) => o.name)).not.toContain("later");
    expect(outputs).toContainEqual({ name: "spare", visible: false });
    expect(wiredOutputNames(saved, "n-human")).toEqual([]);
  });

  test("cancelling the settings after a confirmed delete keeps the output and its edge", async () => {
    const { calls, dialog } = await openNode(
      { template: templateWith(signOff(), ["later"]) },
      "Sign Off",
    );
    await confirmDelete(dialog, outputField(dialog, "later"));

    const settingsCancel = dialog.querySelector(".node-settings-btn-cancel") as HTMLElement;
    fireEvent.click(settingsCancel);
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Node Settings" })).toBeNull());

    fireEvent.click(screen.getByText("Sign Off"));
    const reopened = await screen.findByRole("dialog", { name: "Node Settings" });
    expect(outputField(reopened, "later")).toBeTruthy();

    const saved = await saveLoop(reopened, calls);
    expect(outputsOf(saved, "n-human")).toContainEqual({ name: "later", color: "red" });
    expect(wiredOutputNames(saved, "n-human")).toEqual(["later"]);
  });

  test("an output nothing uses is deleted without asking", async () => {
    const { calls, dialog } = await openNode(
      { template: templateWith(signOff(), ["later"]) },
      "Sign Off",
    );

    fireEvent.click(removeButtonFor(outputField(dialog, "spare")));

    expect(screen.queryByRole("dialog", { name: "Delete output" })).toBeNull();
    const saved = await saveLoop(dialog, calls);
    const outputs = outputsOf(saved, "n-human");
    expect(outputs.map((o) => o.name)).not.toContain("spare");
    expect(outputs).toContainEqual({ name: "later", color: "red" });
    expect(wiredOutputNames(saved, "n-human")).toEqual(["later"]);
  });

  test.each([
    ["no output", "", "Name the output this rule routes to."],
    [
      "success",
      "OnSuccess",
      "'OnSuccess' is taken by the success and failure edges; route to a named output.",
    ],
  ])("a match rule routing to %s is an error in the settings", async (_, name, message) => {
    const { calls, dialog } = await openNode(
      { template: templateWith(reviewer(), ["reject"]) },
      "Reviewer",
    );
    const ruleOutput = within(dialog).getByLabelText("Output name 1");

    fireEvent.change(ruleOutput, { target: { value: name } });

    expect(within(dialog).getByText(message)).toBeTruthy();
    expect(ruleOutput.getAttribute("aria-invalid")).toBe("true");
    expectSaveRefused(dialog);
    expect(screen.queryByRole("dialog", { name: "Remove edges" })).toBeNull();

    fireEvent.change(ruleOutput, { target: { value: "reject" } });
    expectSaveAvailable(dialog);
    const saved = await saveLoop(dialog, calls);
    expect(configOf(saved, "n-ai").matchRules).toEqual([{ pattern: "REJECT", edgeName: "reject" }]);
  });

  test("a case with no output is an error in the settings", async () => {
    const { dialog } = await openNode(
      { template: templateWith(gate(), ["has-pr", "otherwise"]) },
      "Gate",
    );
    const caseOutput = within(dialog).getByLabelText("Case 1 output");

    fireEvent.change(caseOutput, { target: { value: " " } });

    expect(within(dialog).getByText("Name the output this case routes to.")).toBeTruthy();
    expectSaveRefused(dialog);
    fireEvent.change(caseOutput, { target: { value: "has-pr" } });
    expectSaveAvailable(dialog);
  });

  test("a Condition with no default opens with the default blank and the error showing", async () => {
    const { defaultEdge: _dropped, ...withoutDefault } = gate().config;
    const { dialog } = await openNode(
      {
        template: templateWith({ ...gate(), config: withoutDefault }, ["has-pr", "otherwise"]),
      },
      "Gate",
    );

    expect((within(dialog).getByLabelText("Default output") as HTMLInputElement).value).toBe("");
    expect(within(dialog).getByText(/default output is required/i)).toBeTruthy();
    expectSaveRefused(dialog);
  });

  test("a blank output name is an error to fix, not a delete", async () => {
    const { calls, dialog } = await openNode(
      { template: templateWith(signOff(), ["later"]) },
      "Sign Off",
    );
    const field = outputField(dialog, "later");

    fireEvent.change(field, { target: { value: "  " } });

    expect(screen.queryByRole("dialog", { name: "Delete output" })).toBeNull();
    expect(within(dialog).getByText("Give the output a name.")).toBeTruthy();
    expect(field.getAttribute("aria-invalid")).toBe("true");
    expectSaveRefused(dialog);

    fireEvent.change(field, { target: { value: "later" } });
    expectSaveAvailable(dialog);
    const saved = await saveLoop(dialog, calls);
    expect(outputsOf(saved, "n-human")).toContainEqual({ name: "later", color: "red" });
    expect(wiredOutputNames(saved, "n-human")).toEqual(["later"]);
  });

  test.each([
    ["another output", "later"],
    ["success", "OnSuccess"],
  ])("renaming an output to the name of %s is refused in the modal", async (_, taken) => {
    const { dialog } = await openNode({ template: templateWith(signOff(), ["later"]) }, "Sign Off");

    fireEvent.change(outputField(dialog, "spare"), { target: { value: taken } });

    expect(
      within(dialog).getByText(
        `The node already has an output named '${taken}'. Pick another name.`,
      ),
    ).toBeTruthy();
    expectSaveRefused(dialog);
  });
});

describe("Loop Editor — visible to user", () => {
  const node = (type: NodeType, label: string, config: Record<string, unknown>): TemplateNode => ({
    id: "n-node",
    type,
    label,
    config,
  });

  const cmd = (config: Record<string, unknown> = {}) =>
    node(NodeType.Cmd, "Build", { command: "make", ...config });

  /**
   * The dialog has one eye per name: open for exactly the names in `visible`,
   * and crossed out and not clickable for those in `unwired`.
   */
  function expectToggles(
    dialog: HTMLElement,
    visible: string[],
    hidden: string[],
    unwired: string[] = [],
  ) {
    for (const name of visible) expect(isOn(toggle(dialog, name)), name).toBe(true);
    for (const name of hidden) expect(isOn(toggle(dialog, name)), name).toBe(false);
    for (const name of unwired) {
      expect(isOn(toggle(dialog, name)), name).toBe(false);
      expect(toggle(dialog, name).disabled, name).toBe(true);
    }
    expect(toggles(dialog)).toHaveLength(visible.length + hidden.length + unwired.length);
  }

  async function saveNodeAndReopen(dialog: HTMLElement, label: string) {
    fireEvent.click(within(dialog).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Node Settings" })).toBeNull());
    fireEvent.click(screen.getByText(label));
    return await screen.findByRole("dialog", { name: "Node Settings" });
  }

  async function cancelAndReopen(dialog: HTMLElement, label: string) {
    fireEvent.click(dialog.querySelector(".node-settings-btn-cancel") as HTMLElement);
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Node Settings" })).toBeNull());
    fireEvent.click(screen.getByText(label));
    return await screen.findByRole("dialog", { name: "Node Settings" });
  }

  function hasVisible(outputs: unknown) {
    return ((outputs as Output[] | undefined) ?? []).filter((o) => "visible" in o);
  }

  const OTHER_RESERVED = RESERVED.filter((name) => name !== "on_merged");

  const TOGGLE_CASES: Array<{
    name: string;
    node: TemplateNode;
    failureWired?: boolean;
    visible: string[];
    hidden: string[];
    unwired: string[];
  }> = [
    {
      name: "a PR node with no visible fields hides its reserved outputs, declared or not",
      node: node(NodeType.PR, "Pull Request", {
        outputs: [{ name: "deploy" }, { name: "on_ci_failed", reserved: true }],
      }),
      visible: ["OnSuccess", "deploy"],
      hidden: RESERVED,
      unwired: ["OnFailure"],
    },
    {
      name: "a PR node shows what its outputs say",
      node: node(NodeType.PR, "Pull Request", {
        outputs: [
          { name: "OnSuccess", visible: false },
          { name: "OnFailure" },
          { name: "on_merged", reserved: true, visible: true },
          { name: "on_ci_failed", reserved: true, visible: false },
          { name: "deploy", visible: false },
        ],
      }),
      visible: ["on_merged"],
      hidden: ["OnSuccess", "deploy", ...OTHER_RESERVED],
      unwired: ["OnFailure"],
    },
    {
      name: "a PR node's wired failure output can be hidden",
      node: node(NodeType.PR, "Pull Request", {
        outputs: [{ name: "OnFailure", visible: false }],
      }),
      failureWired: true,
      visible: ["OnSuccess"],
      hidden: ["OnFailure", ...RESERVED],
      unwired: [],
    },
    {
      name: "a Human output named like a reserved one, or with a visible that is not a boolean, is visible",
      node: node(NodeType.Human, "Sign Off", {
        outputs: [
          { name: "on_merged" },
          { name: "later", visible: "no" },
          { name: "never", visible: 0 },
        ],
      }),
      visible: ["OnSuccess", "on_merged", "later", "never"],
      hidden: [],
      unwired: ["OnFailure"],
    },
    {
      name: "a Human node's unwired failure output shows as hidden whatever it says",
      node: node(NodeType.Human, "Sign Off", {
        outputs: [{ name: "OnFailure", visible: true }],
      }),
      visible: ["OnSuccess"],
      hidden: [],
      unwired: ["OnFailure"],
    },
  ];

  test.each(TOGGLE_CASES)(
    "$name, and every wired eye flips both ways",
    async ({ node: opened, failureWired, visible, hidden, unwired }) => {
      const { dialog } = await openNode(
        { template: templateWith(opened, [], { failureWired }) },
        opened.label,
      );

      await waitFor(() => expectToggles(dialog, visible, hidden, unwired));

      for (const name of [...visible, ...hidden]) {
        const before = isOn(toggle(dialog, name));
        expect(toggle(dialog, name).disabled, name).toBe(false);
        fireEvent.click(toggle(dialog, name));
        expect(isOn(toggle(dialog, name)), name).toBe(!before);
        fireEvent.click(toggle(dialog, name));
        expect(isOn(toggle(dialog, name)), name).toBe(before);
      }
      expectToggles(dialog, visible, hidden, unwired);
    },
  );

  test.each([
    ["a Cmd node", cmd(), "Build"],
    ["a Start node", cmd(), "Initialize"],
    ["a Cleanup node", cmd(), "Tidy Up"],
    [
      "an AI node",
      node(NodeType.AI, "Reviewer", { prompt: "p", outputs: [{ name: "x", visible: false }] }),
      "Reviewer",
    ],
    [
      "a Condition node",
      node(NodeType.Condition, "Gate", {
        cases: [{ variant: "PrExists", edgeName: "has-pr" }],
        defaultEdge: "otherwise",
        outputs: [{ name: "OnFailure" }, { name: "has-pr" }, { name: "otherwise" }],
      }),
      "Gate",
    ],
    ["a Prompt node", node(NodeType.Prompt, "Brief", { prompt: "p" }), "Brief"],
  ])("%s has no eye and no success and failure rows", async (_, opened, label) => {
    const { dialog } = await openNode({ template: templateWith(opened) }, label);

    expect(toggles(dialog)).toHaveLength(0);
    expect(within(dialog).queryByText("Success and failure")).toBeNull();
    expect(within(dialog).queryAllByDisplayValue("OnSuccess")).toHaveLength(0);
    expect(within(dialog).queryAllByDisplayValue("OnFailure")).toHaveLength(0);
  });

  test("the eye names its state and the exact output, sits before the shield and the remove button, and mutes a hidden row", async () => {
    const human = node(NodeType.Human, "Sign Off", {
      outputs: [{ name: "later", visible: false }],
    });
    const { dialog } = await openNode({ template: templateWith(human) }, "Sign Off");

    const later = toggle(dialog, "later");
    expect(later.tagName).toBe("BUTTON");
    expect(later.type).toBe("button");
    expect(later.getAttribute("aria-label")).toBe("Hidden from user: later");
    expect(later.title).toBe("Hidden from user: later");
    expect(later.getAttribute("aria-pressed")).toBe("false");
    const row = later.closest(".match-rule-row") as HTMLElement;
    expect(row.classList.contains("output-row-hidden")).toBe(true);
    const buttons = within(row).getAllByRole("button");
    const remove = buttons.indexOf(removeButtonFor(outputField(dialog, "later")));
    expect(buttons.indexOf(later)).toBe(remove - 2);
    expect(buttons[remove - 1].classList.contains("output-confirm-toggle")).toBe(true);

    fireEvent.click(later);
    expect(later.getAttribute("aria-label")).toBe("Visible to user: later");
    expect(later.title).toBe("Visible to user: later");
    expect(later.getAttribute("aria-pressed")).toBe("true");
    expect(row.classList.contains("output-row-hidden")).toBe(false);

    const failure = toggle(dialog, "OnFailure");
    expect(failure.title).toBe("Hidden from user: OnFailure (no edge connected)");
    expect(failure.disabled).toBe(true);
  });

  test("a reserved output's name stays read-only beside its eye", async () => {
    const pr = node(NodeType.PR, "Pull Request", {
      outputs: [{ name: "on_merged", reserved: true }],
    });
    const { dialog } = await openNode({ template: templateWith(pr) }, "Pull Request");
    await waitFor(() => expect(isOn(toggle(dialog, "on_abandoned"))).toBe(false));

    fireEvent.click(toggle(dialog, "on_merged"));
    fireEvent.click(toggle(dialog, "on_abandoned"));

    for (const name of ["on_merged", "on_abandoned"]) {
      expect(isOn(toggle(dialog, name))).toBe(true);
      const fields = within(dialog).queryAllByDisplayValue(name);
      expect(fields.length).toBeGreaterThan(0);
      for (const field of fields) expect(isEditable(field)).toBe(false);
    }
  });

  test("saving writes visible only where it differs from the default, onto that output alone", async () => {
    const pr = node(NodeType.PR, "Pull Request", {
      prDescriptionTemplate: "t",
      outputs: [
        { name: "OnSuccess" },
        { name: "OnFailure", visible: false, color: "x" },
        { name: "deploy", color: "blue" },
        { name: "on_merged", reserved: true, visible: true },
        { name: "on_ci_failed", reserved: true },
        { name: "on_comment", reserved: true, visible: true },
      ],
    });
    const { calls, dialog } = await openNode(
      { template: templateWith(pr, ["deploy", "on_merged"], { failureWired: true }) },
      "Pull Request",
    );
    await waitFor(() => expect(isOn(toggle(dialog, "on_abandoned"))).toBe(false));

    fireEvent.click(toggle(dialog, "OnSuccess"));
    fireEvent.click(toggle(dialog, "OnFailure"));
    fireEvent.click(toggle(dialog, "deploy"));
    fireEvent.click(toggle(dialog, "on_merged"));
    fireEvent.click(toggle(dialog, "on_ci_failed"));
    fireEvent.click(toggle(dialog, "on_abandoned"));

    // The choices are the node's as soon as its settings are saved.
    const reopened = await saveNodeAndReopen(dialog, "Pull Request");
    expectToggles(
      reopened,
      ["OnFailure", "on_ci_failed", "on_comment", "on_abandoned"],
      [
        "OnSuccess",
        "deploy",
        ...RESERVED.filter(
          (name) => !["on_ci_failed", "on_comment", "on_abandoned"].includes(name),
        ),
      ],
    );

    const saved = await saveLoop(reopened, calls);
    const outputs = outputsOf(saved, "n-node");
    expect(outputs.slice(0, 6)).toEqual([
      { name: "OnSuccess", visible: false },
      { name: "OnFailure", color: "x" },
      { name: "deploy", color: "blue", visible: false },
      { name: "on_merged", reserved: true },
      { name: "on_ci_failed", reserved: true, visible: true },
      { name: "on_comment", reserved: true, visible: true },
    ]);
    const rest = outputs.slice(6);
    expect(rest.filter((o) => o.name === "on_abandoned")).toHaveLength(1);
    expect(rest.find((o) => o.name === "on_abandoned")).toMatchObject({ visible: true });
    expect(hasVisible(rest).map((o) => o.name)).toEqual(["on_abandoned"]);
    expect(configOf(saved, "n-node").prDescriptionTemplate).toBe("t");

    // Visibility is the output's, not the edge's: the edges are sent as they were.
    const wired = saved.edges.filter(
      (e) => e.sourceNodeId === "n-node" && e.edgeType === EdgeType.Custom,
    );
    expect(wired.map((e) => e.name)).toEqual(["deploy", "on_merged"]);
    for (const edge of saved.edges) expect(edge).not.toHaveProperty("visible");
  });

  test.each([
    [
      "a PR node",
      node(NodeType.PR, "Pull Request", {
        outputs: [{ name: "deploy" }, { name: "on_merged", reserved: true }],
      }),
    ],
    ["a Human node", node(NodeType.Human, "Sign Off", { outputs: [{ name: "later" }] })],
  ])("saving %s without touching an eye writes no visible", async (_, opened) => {
    const { calls, dialog } = await openNode({ template: templateWith(opened) }, opened.label);
    await waitFor(() => expect(isOn(toggle(dialog, "OnSuccess"))).toBe(true));

    const saved = await saveLoop(dialog, calls);

    expect(hasVisible(configOf(saved, "n-node").outputs)).toEqual([]);
  });

  test.each([
    [
      "a Cmd node",
      cmd({
        outputs: [
          { name: "OnSuccess", color: "g", visible: false },
          { name: "extra", note: "kept" },
          { name: "OnFailure", visible: false },
        ],
      }),
      "Build",
    ],
    [
      "an AI node",
      node(NodeType.AI, "Reviewer", {
        prompt: "p",
        matchRules: [{ pattern: "X", edgeName: "x" }],
        outputs: [
          { name: "OnSuccess", visible: false },
          { name: "OnFailure" },
          { name: "x", visible: true },
        ],
      }),
      "Reviewer",
    ],
  ])("saving %s keeps the visible its outputs already carry", async (_, opened, label) => {
    const before = structuredClone(opened.config.outputs);
    const { calls, dialog } = await openNode({ template: templateWith(opened) }, label);

    const saved = await saveLoop(dialog, calls);

    expect(outputsOf(saved, "n-node")).toEqual(before);
  });

  test("hiding an output the config does not declare yet declares it", async () => {
    const human = node(NodeType.Human, "Sign Off", {});
    const { calls, dialog } = await openNode(
      { template: templateWith(human, [], { failureWired: true }) },
      "Sign Off",
    );
    await waitFor(() => expect(isOn(toggle(dialog, "OnFailure"))).toBe(true));

    fireEvent.click(toggle(dialog, "OnFailure"));

    const saved = await saveLoop(dialog, calls);
    const outputs = outputsOf(saved, "n-node");
    expect(outputs.filter((o) => o.name === "OnFailure")).toEqual([
      { name: "OnFailure", visible: false },
    ]);
    expect(hasVisible(outputs).map((o) => o.name)).toEqual(["OnFailure"]);
  });

  test("cancelling the settings discards the eye changes", async () => {
    const human = node(NodeType.Human, "Sign Off", {
      outputs: [{ name: "later" }, { name: "never", visible: false }],
    });
    const { calls, dialog } = await openNode(
      { template: templateWith(human, [], { failureWired: true }) },
      "Sign Off",
    );
    await waitFor(() => expectToggles(dialog, ["OnSuccess", "OnFailure", "later"], ["never"]));

    fireEvent.click(toggle(dialog, "OnSuccess"));
    fireEvent.click(toggle(dialog, "later"));
    fireEvent.click(toggle(dialog, "never"));
    expectToggles(dialog, ["OnFailure", "never"], ["OnSuccess", "later"]);

    const reopened = await cancelAndReopen(dialog, "Sign Off");
    expectToggles(reopened, ["OnSuccess", "OnFailure", "later"], ["never"]);

    const saved = await saveLoop(reopened, calls);
    expect(hasVisible(outputsOf(saved, "n-node"))).toEqual([{ name: "never", visible: false }]);
  });

  test("an output renamed in the same edit keeps the visibility chosen for it", async () => {
    const human = node(NodeType.Human, "Sign Off", {
      outputs: [{ name: "later", color: "x" }, { name: "now" }],
    });
    const { calls, dialog } = await openNode({ template: templateWith(human) }, "Sign Off");
    await waitFor(() => expect(isOn(toggle(dialog, "later"))).toBe(true));

    fireEvent.click(toggle(dialog, "later"));
    fireEvent.change(outputField(dialog, "later"), { target: { value: "deferred" } });

    expect(isOn(toggle(dialog, "deferred"))).toBe(false);
    expect(isOn(toggle(dialog, "now"))).toBe(true);
    expect(queryToggle(dialog, "later")).toBeNull();

    const saved = await saveLoop(dialog, calls);
    const outputs = outputsOf(saved, "n-node");
    expect(outputs.find((o) => o.name === "deferred")).toEqual({
      name: "deferred",
      color: "x",
      visible: false,
    });
    expect(hasVisible(outputs).map((o) => o.name)).toEqual(["deferred"]);
  });

  /** No id is empty, holds whitespace or repeats, and every id reference resolves. */
  function expectWellFormedIds(dialog: HTMLElement) {
    const ids = [...dialog.querySelectorAll("[id]")].map((el) => el.getAttribute("id") ?? "");
    for (const id of ids) expect(id).toMatch(/^\S+$/);
    expect(new Set(ids).size).toBe(ids.length);
    for (const attribute of ["aria-describedby", "aria-labelledby", "for"]) {
      for (const el of dialog.querySelectorAll(`[${attribute}]`)) {
        const references = (el.getAttribute(attribute) ?? "").split(/\s+/).filter(Boolean);
        expect(references.length).toBeGreaterThan(0);
        for (const id of references) expect(document.getElementById(id), id).not.toBeNull();
      }
    }
  }

  test("outputs named like Object.prototype members, or with spaces, each have an eye of their own", async () => {
    const names = ["constructor", "toString", "__proto__", "hasOwnProperty", "needs more work"];
    const human = node(NodeType.Human, "Sign Off", {
      outputs: [
        { name: "OnSuccess" },
        { name: "OnFailure" },
        ...names.map((name) => ({ name, color: name })),
      ],
    });
    const { calls, dialog } = await openNode(
      { template: templateWith(human, [], { failureWired: true }) },
      "Sign Off",
    );
    const all = ["OnSuccess", "OnFailure", ...names];
    await waitFor(() => expectToggles(dialog, all, []));
    expectWellFormedIds(dialog);

    fireEvent.click(toggle(dialog, "toString"));
    expectToggles(
      dialog,
      all.filter((name) => name !== "toString"),
      ["toString"],
    );

    fireEvent.click(toggle(dialog, "__proto__"));
    fireEvent.click(toggle(dialog, "needs more work"));
    fireEvent.click(toggle(dialog, "hasOwnProperty"));
    fireEvent.click(toggle(dialog, "hasOwnProperty"));
    expectToggles(
      dialog,
      ["OnSuccess", "OnFailure", "constructor", "hasOwnProperty"],
      ["toString", "__proto__", "needs more work"],
    );
    expectWellFormedIds(dialog);

    const saved = await saveLoop(dialog, calls);
    const outputs = outputsOf(saved, "n-node");
    expect(outputs.map((o) => [o.name, o.color, "visible" in o ? o.visible : "-"])).toEqual([
      ["OnSuccess", undefined, "-"],
      ["OnFailure", undefined, "-"],
      ["constructor", "constructor", "-"],
      ["toString", "toString", false],
      ["__proto__", "__proto__", false],
      ["hasOwnProperty", "hasOwnProperty", "-"],
      ["needs more work", "needs more work", false],
    ]);
  });

  test("success and a named output called 'success' have eyes that are told apart", async () => {
    const human = node(NodeType.Human, "Sign Off", {
      outputs: [{ name: "OnSuccess" }, { name: "OnFailure" }, { name: "success" }],
    });
    const { calls, dialog } = await openNode(
      { template: templateWith(human, [], { failureWired: true }) },
      "Sign Off",
    );
    await waitFor(() => expectToggles(dialog, ["OnSuccess", "OnFailure", "success"], []));

    expect(toggle(dialog, "success")).not.toBe(toggle(dialog, "OnSuccess"));
    fireEvent.click(toggle(dialog, "success"));
    expectToggles(dialog, ["OnSuccess", "OnFailure"], ["success"]);
    fireEvent.click(toggle(dialog, "OnSuccess"));
    fireEvent.click(toggle(dialog, "success"));
    expectToggles(dialog, ["OnFailure", "success"], ["OnSuccess"]);

    const saved = await saveLoop(dialog, calls);
    expect(outputsOf(saved, "n-node")).toEqual([
      { name: "OnSuccess", visible: false },
      { name: "OnFailure" },
      { name: "success" },
    ]);
  });
});

/** The shield button of the output called exactly `name`. */
function shield(dialog: HTMLElement, name: string) {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const label = new RegExp(`^(Asks|Does not ask) to confirm: ${escaped}( \\(.*\\))?$`);
  const found = within(dialog).queryAllByRole("button", { name: label });
  expect(found, name).toHaveLength(1);
  return found[0] as HTMLButtonElement;
}

function shields(dialog: HTMLElement) {
  return within(dialog).queryAllByRole("button", {
    name: /^(Asks|Does not ask) to confirm: /,
  }) as HTMLButtonElement[];
}

describe("Loop Editor — asks to confirm", () => {
  const human = (config: Record<string, unknown>): TemplateNode => ({
    id: "n-node",
    type: NodeType.Human,
    label: "Sign Off",
    config,
  });

  test("every output starts not asking, and a hidden or unwired one cannot be switched on", async () => {
    const { dialog } = await openNode(
      {
        template: templateWith(
          human({
            outputs: [{ name: "later" }, { name: "never", visible: false }, { name: "spare" }],
          }),
          ["later", "never"],
        ),
      },
      "Sign Off",
    );

    await waitFor(() => expect(shields(dialog)).toHaveLength(5));
    for (const button of shields(dialog)) expect(isOn(button)).toBe(false);
    expect(shield(dialog, "OnSuccess").disabled).toBe(false);
    expect(shield(dialog, "later").disabled).toBe(false);
    expect(shield(dialog, "never").title).toBe("Does not ask to confirm: never (hidden from user)");
    expect(shield(dialog, "never").disabled).toBe(true);
    expect(shield(dialog, "spare").title).toBe(
      "Does not ask to confirm: spare (no edge connected)",
    );
    expect(shield(dialog, "spare").disabled).toBe(true);
    expect(shield(dialog, "OnFailure").title).toBe(
      "Does not ask to confirm: OnFailure (no edge connected)",
    );
    expect(shield(dialog, "OnFailure").disabled).toBe(true);

    fireEvent.click(toggle(dialog, "later"));
    expect(shield(dialog, "later").disabled).toBe(true);
  });

  test("an output added in this edit has no edge yet, and a renamed one keeps its edge", async () => {
    const { dialog } = await openNode(
      { template: templateWith(human({ outputs: [{ name: "later" }] }), ["later"]) },
      "Sign Off",
    );
    await waitFor(() => expect(shields(dialog)).toHaveLength(3));

    fireEvent.change(outputField(dialog, "later"), { target: { value: "afterwards" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "+ Add output" }));
    const fields = within(dialog).getAllByPlaceholderText("Output name");
    const added = fields[fields.length - 1];
    fireEvent.change(added, { target: { value: "fresh" } });

    expect(shield(dialog, "afterwards").disabled).toBe(false);
    expect(shield(dialog, "fresh").title).toBe(
      "Does not ask to confirm: fresh (no edge connected)",
    );
    expect(shield(dialog, "fresh").disabled).toBe(true);
  });

  test("switching the shield on saves confirm onto that output alone, declaring success if it must", async () => {
    const { calls, dialog } = await openNode(
      {
        template: templateWith(
          human({ outputs: [{ name: "cleanup", note: "kept" }, { name: "later" }] }),
          ["cleanup", "later"],
        ),
      },
      "Sign Off",
    );
    await waitFor(() => expect(shields(dialog)).toHaveLength(4));

    fireEvent.click(shield(dialog, "cleanup"));
    fireEvent.click(shield(dialog, "OnSuccess"));
    expect(shield(dialog, "cleanup").title).toBe("Asks to confirm: cleanup");
    expect(isOn(shield(dialog, "OnSuccess"))).toBe(true);

    const saved = await saveLoop(dialog, calls);
    expect(outputsOf(saved, "n-node")).toEqual([
      { name: "cleanup", note: "kept", confirm: true },
      { name: "later" },
      { name: "OnSuccess", confirm: true },
    ]);
  });

  test("switching it off again removes confirm, and hiding an output keeps it", async () => {
    const { calls, dialog } = await openNode(
      {
        template: templateWith(
          human({
            outputs: [
              { name: "OnSuccess", confirm: true },
              { name: "cleanup", confirm: true },
              { name: "pr", confirm: true },
            ],
          }),
          ["cleanup", "pr"],
        ),
      },
      "Sign Off",
    );
    await waitFor(() => expect(isOn(shield(dialog, "cleanup"))).toBe(true));

    fireEvent.click(shield(dialog, "OnSuccess"));
    fireEvent.click(shield(dialog, "cleanup"));
    fireEvent.click(toggle(dialog, "pr"));

    const saved = await saveLoop(dialog, calls);
    expect(outputsOf(saved, "n-node")).toEqual([
      { name: "OnSuccess" },
      { name: "cleanup" },
      { name: "pr", confirm: true, visible: false },
    ]);
  });

  test("cancelling the settings discards the shield changes", async () => {
    const { dialog } = await openNode(
      { template: templateWith(human({ outputs: [{ name: "cleanup" }] }), ["cleanup"]) },
      "Sign Off",
    );
    await waitFor(() => expect(shields(dialog)).toHaveLength(3));

    fireEvent.click(shield(dialog, "cleanup"));
    fireEvent.click(shield(dialog, "OnSuccess"));
    fireEvent.click(dialog.querySelector(".node-settings-btn-cancel") as HTMLElement);
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Node Settings" })).toBeNull());
    fireEvent.click(screen.getByText("Sign Off"));
    const reopened = await screen.findByRole("dialog", { name: "Node Settings" });

    for (const button of shields(reopened)) expect(isOn(button)).toBe(false);
  });

  test.each([
    [
      "a Cmd node",
      { id: "n-node", type: NodeType.Cmd, label: "Build", config: { command: "make" } },
    ],
    [
      "an AI node",
      {
        id: "n-node",
        type: NodeType.AI,
        label: "Reviewer",
        config: { prompt: "p", outputs: [{ name: "x", confirm: true }] },
      },
    ],
  ] as Array<[string, TemplateNode]>)("%s has no shield", async (_, opened) => {
    const { dialog } = await openNode({ template: templateWith(opened) }, opened.label);

    expect(shields(dialog)).toHaveLength(0);
  });
});
