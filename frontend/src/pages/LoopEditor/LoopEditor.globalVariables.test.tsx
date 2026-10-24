import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, cleanup, fireEvent, within, act } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import { EdgeType, NodeType, RecoveryPolicy } from "../../types";

// The prompt editors offer the global variables that exist when the editor opens.
// They are a convenience: when they cannot be loaded, the editor still works.

const { loopTemplateService, aiProviderService, globalVariableService } = vi.hoisted(() => ({
  loopTemplateService: {
    getAll: vi.fn(),
    validate: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    getNodeOutputs: vi.fn(),
  },
  aiProviderService: { getAll: vi.fn() },
  globalVariableService: { getAll: vi.fn() },
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
  globalVariableService,
}));

import LoopEditor from "./index";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";

const template = {
  id: "tpl-1",
  name: "Dev Loop",
  description: "",
  version: 1,
  recoveryPolicy: RecoveryPolicy.AutoResume,
  nodes: [
    { id: "n-start", type: NodeType.Start, label: "Initialize", config: {} },
    { id: "n-ai", type: NodeType.AI, label: "Reviewer", config: { prompt: "" } },
    { id: "n-cleanup", type: NodeType.Cleanup, label: "Tidy Up", config: {} },
  ],
  edges: [
    { id: "e-1", sourceNodeId: "n-start", targetNodeId: "n-ai", edgeType: EdgeType.OnSuccess },
    { id: "e-2", sourceNodeId: "n-ai", targetNodeId: "n-cleanup", edgeType: EdgeType.OnSuccess },
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

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

async function typeInAiPrompt(text: string) {
  loopTemplateService.getAll.mockResolvedValue([template]);
  loopTemplateService.validate.mockResolvedValue({ valid: true, errors: [] });
  loopTemplateService.getNodeOutputs.mockResolvedValue({});
  aiProviderService.getAll.mockResolvedValue([]);

  render(
    <MemoryRouter initialEntries={["/loop-editor/tpl-1"]}>
      <AuthContext.Provider value={authValue}>
        <Routes>
          <Route path="/loop-editor/:templateId" element={<LoopEditor />} />
        </Routes>
      </AuthContext.Provider>
    </MemoryRouter>,
    { wrapper: WorkItemHubProvider },
  );

  await waitFor(() => expect(screen.getByText("Reviewer")).toBeTruthy());
  await waitFor(() => expect(globalVariableService.getAll).toHaveBeenCalled());
  await act(async () => {});
  fireEvent.click(screen.getByText("Reviewer"));
  const dialog = await screen.findByRole("dialog", { name: "Node Settings" });
  const prompt = dialog.querySelector("#ai-prompt") as HTMLTextAreaElement;
  prompt.selectionStart = text.length;
  prompt.selectionEnd = text.length;
  fireEvent.change(prompt, { target: { value: text } });
  return within(dialog)
    .getAllByRole("listitem")
    .map((li) => li.textContent!.match(/\{\{[^}]*\}\}/)![0])
    .sort();
}

describe("LoopEditor global variables in prompts", () => {
  test("offers the global variables that exist", async () => {
    globalVariableService.getAll.mockResolvedValue([
      {
        id: "g-1",
        name: "review_rules",
        value: "be brief",
        updatedAt: "2026-10-01T10:00:00Z",
        usedBy: [],
      },
      { id: "g-2", name: "writing", value: "", updatedAt: "2026-10-01T10:00:00Z", usedBy: [] },
    ]);

    const offered = await typeInAiPrompt("{{Gl");

    expect(offered).toEqual(["{{Global.review_rules}}", "{{Global.writing}}", "{{Global.}}"]);
  });

  test("offers only the bare placeholder and shows no error when they cannot be loaded", async () => {
    globalVariableService.getAll.mockRejectedValue({ status: 500, message: "boom" });

    const offered = await typeInAiPrompt("{{Gl");

    expect(offered).toEqual(["{{Global.}}"]);
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.queryByText(/boom/)).toBeNull();
  });
});
