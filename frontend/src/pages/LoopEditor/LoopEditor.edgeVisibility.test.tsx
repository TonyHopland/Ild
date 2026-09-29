import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, cleanup, fireEvent, within, act } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import { EdgeType, NodeType, RecoveryPolicy } from "../../types";

// Each outgoing edge of a node gets a "Visible to user" toggle in the node's
// settings. It shows the stored value — or, for an edge that has none yet, the
// creation default — and only Save in the modal commits a change to the loop.

const { loopTemplateService, aiProviderService } = vi.hoisted(() => ({
  loopTemplateService: {
    getAll: vi.fn(),
    validate: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
  },
  aiProviderService: { getAll: vi.fn() },
}));

vi.mock("../../hooks/useSignalR", () => ({
  useSignalR: () => ({
    connectionState: "connected",
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn(() => Promise.resolve()),
  }),
}));

vi.mock("../../services/auth", () => ({
  loopTemplateService,
  aiProviderService,
}));

import LoopEditor from "./index";

// "e-pr-merged" carries no userVisible: an edge not saved yet (as a chat-applied
// loop document leaves it), so its toggle shows the creation default.
const template = {
  id: "tpl-1",
  name: "Dev Loop",
  description: "",
  version: 1,
  recoveryPolicy: RecoveryPolicy.AutoResume,
  nodes: [
    { id: "n-start", type: NodeType.Start, label: "Initialize", config: {} },
    { id: "n-pr", type: NodeType.PR, label: "Open PR", config: {} },
    { id: "n-fix", type: NodeType.AI, label: "Fixer", config: { prompt: "fix" } },
    { id: "n-cleanup", type: NodeType.Cleanup, label: "Tidy Up", config: {} },
  ],
  edges: [
    {
      id: "e-start",
      sourceNodeId: "n-start",
      targetNodeId: "n-pr",
      edgeType: EdgeType.OnSuccess,
      userVisible: true,
    },
    {
      id: "e-pr-ci",
      sourceNodeId: "n-pr",
      targetNodeId: "n-fix",
      edgeType: EdgeType.Custom,
      name: "on_ci_failed",
      userVisible: true,
    },
    {
      id: "e-pr-merged",
      sourceNodeId: "n-pr",
      targetNodeId: "n-cleanup",
      edgeType: EdgeType.Custom,
      name: "on_merged",
    },
    {
      id: "e-pr-ok",
      sourceNodeId: "n-pr",
      targetNodeId: "n-cleanup",
      edgeType: EdgeType.OnSuccess,
      userVisible: false,
    },
    {
      id: "e-fix",
      sourceNodeId: "n-fix",
      targetNodeId: "n-cleanup",
      edgeType: EdgeType.OnSuccess,
      userVisible: true,
    },
  ],
  createdAt: "2025-01-01T00:00:00Z",
  updatedAt: "2025-01-01T00:00:00Z",
  isArchived: false,
};

const authValue = {
  user: { id: "1", username: "test", createdAt: "" },
  token: "test-token",
  isAuthenticated: true,
  isLoading: false,
  login: vi.fn(),
  logout: vi.fn(),
};

async function renderEditor(loaded = template) {
  loopTemplateService.getAll.mockResolvedValue([loaded]);
  loopTemplateService.validate.mockResolvedValue({ valid: true, errors: [] });
  loopTemplateService.update.mockResolvedValue({ id: "tpl-1" });
  aiProviderService.getAll.mockResolvedValue([]);

  render(
    <MemoryRouter initialEntries={["/loop-editor/tpl-1"]}>
      <AuthContext.Provider value={authValue}>
        <Routes>
          <Route path="/loop-editor" element={<LoopEditor />} />
          <Route path="/loop-editor/:templateId" element={<LoopEditor />} />
        </Routes>
      </AuthContext.Provider>
    </MemoryRouter>,
  );

  await waitFor(() => expect(screen.getByText("Open PR")).toBeTruthy());
  await waitFor(() => expect(aiProviderService.getAll).toHaveBeenCalled());
  await act(async () => {});
}

async function openNode(label: string) {
  fireEvent.click(screen.getByText(label));
  return screen.findByRole("dialog", { name: "Node Settings" });
}

function toggle(dialog: HTMLElement, edgeLabel: string) {
  return within(dialog).getByRole("checkbox", {
    name: `Visible to user: ${edgeLabel}`,
  }) as HTMLInputElement;
}

async function closeDialogWith(dialog: HTMLElement, button: "Save" | "Cancel" | "Close") {
  const control =
    button === "Close"
      ? within(dialog).getByRole("button", { name: "Close" })
      : within(dialog).getByText(button);
  fireEvent.click(control);
  await waitFor(() => expect(screen.queryByRole("dialog", { name: "Node Settings" })).toBeNull());
}

/** Saves the loop and returns each edge's userVisible as sent. */
async function saveLoopAndReadVisibility() {
  fireEvent.click(screen.getByText("Save"));
  fireEvent.click(await screen.findByText("Save changes"));
  await waitFor(() => expect(loopTemplateService.update).toHaveBeenCalled());
  const calls = loopTemplateService.update.mock.calls;
  const payload = calls[calls.length - 1][1] as {
    edges: Array<{ id: string; userVisible?: boolean }>;
  };
  return Object.fromEntries(payload.edges.map((e) => [e.id, e.userVisible]));
}

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("Loop Editor — Visible to user toggle per edge", () => {
  test("lists each outgoing edge with its target and its stored value or creation default, and nothing for a node without edges", async () => {
    await renderEditor();
    const dialog = await openNode("Open PR");

    expect(toggle(dialog, "on_ci_failed").checked).toBe(true);
    expect(toggle(dialog, "on_merged").checked).toBe(false);
    expect(toggle(dialog, "success").checked).toBe(false);
    expect(within(dialog).getAllByRole("checkbox", { name: /^Visible to user/ })).toHaveLength(3);
    expect(within(dialog).getByText("Fixer")).toBeTruthy();
    expect(within(dialog).getAllByText("Tidy Up").length).toBeGreaterThan(0);
    await closeDialogWith(dialog, "Cancel");

    // A node with no outgoing edges has nothing to list.
    const sink = await openNode("Tidy Up");
    expect(within(sink).queryAllByRole("checkbox", { name: /Visible to user/ })).toHaveLength(0);
  });

  test("Save in the modal commits the toggled values, and saving the loop sends them", async () => {
    await renderEditor();
    let dialog = await openNode("Open PR");
    fireEvent.click(toggle(dialog, "on_merged"));
    fireEvent.click(toggle(dialog, "on_ci_failed"));
    await closeDialogWith(dialog, "Save");

    dialog = await openNode("Open PR");
    expect(toggle(dialog, "on_merged").checked).toBe(true);
    expect(toggle(dialog, "on_ci_failed").checked).toBe(false);
    await closeDialogWith(dialog, "Cancel");

    const sent = await saveLoopAndReadVisibility();
    expect(sent["e-pr-merged"]).toBe(true);
    expect(sent["e-pr-ci"]).toBe(false);
    // Untouched edges keep their stored value, false included.
    expect(sent["e-pr-ok"]).toBe(false);
    expect(sent["e-start"]).toBe(true);
    expect(sent["e-fix"]).toBe(true);
  });

  test.each(["Cancel", "Close"] as const)("%s discards the toggled values", async (button) => {
    await renderEditor();
    let dialog = await openNode("Open PR");
    fireEvent.click(toggle(dialog, "on_merged"));
    fireEvent.click(toggle(dialog, "on_ci_failed"));
    fireEvent.click(toggle(dialog, "success"));
    await closeDialogWith(dialog, button);

    dialog = await openNode("Open PR");
    expect(toggle(dialog, "on_merged").checked).toBe(false);
    expect(toggle(dialog, "on_ci_failed").checked).toBe(true);
    expect(toggle(dialog, "success").checked).toBe(false);
    await closeDialogWith(dialog, "Cancel");

    const sent = await saveLoopAndReadVisibility();
    expect(sent["e-pr-ci"]).toBe(true);
    expect(sent["e-pr-ok"]).toBe(false);
    // Never set: either left for the server's default or sent as that default.
    expect(sent["e-pr-merged"]).not.toBe(true);
  });

  test("an edge id that names an Object member still shows and keeps the edge's own value", async () => {
    // Loop documents let the author choose edge ids, so an id can collide with
    // a member every plain object inherits.
    const renamed: Record<string, string> = { "e-pr-ok": "constructor", "e-pr-ci": "toString" };
    await renderEditor({
      ...template,
      edges: template.edges.map((edge) => ({ ...edge, id: renamed[edge.id] ?? edge.id })),
    });

    let dialog = await openNode("Open PR");
    expect(toggle(dialog, "success").checked).toBe(false);
    expect(toggle(dialog, "on_ci_failed").checked).toBe(true);
    await closeDialogWith(dialog, "Save");

    dialog = await openNode("Open PR");
    fireEvent.click(toggle(dialog, "on_ci_failed"));
    await closeDialogWith(dialog, "Save");

    const sent = await saveLoopAndReadVisibility();
    expect(sent["constructor"]).toBe(false);
    expect(sent["toString"]).toBe(false);
  });
});
