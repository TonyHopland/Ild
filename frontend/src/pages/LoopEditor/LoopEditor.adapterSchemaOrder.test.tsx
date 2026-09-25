import { afterEach, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, cleanup, fireEvent, within, act } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import { ConfigFieldType, EdgeType, NodeType, RecoveryPolicy } from "../../types";
import type { AiProvider, ConfigFieldDescriptor } from "../../types";

const { loopTemplateService, aiProviderService, agentAdapterService } = vi.hoisted(() => ({
  loopTemplateService: { getAll: vi.fn(), validate: vi.fn(), create: vi.fn(), update: vi.fn() },
  aiProviderService: { getAll: vi.fn() },
  agentAdapterService: { getConfigSchema: vi.fn() },
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
  agentAdapterService,
}));

import LoopEditor from "./index";

const provider = (id: string, type: string, isDefault: boolean, tags: string[]): AiProvider => ({
  id,
  name: id,
  type,
  baseUrl: "",
  apiKey: "",
  model: "",
  isDefault,
  parallelism: 0,
  tags,
  supportedTools: [],
  createdAt: "2025-01-01T00:00:00Z",
});

const field = (name: string, label: string): ConfigFieldDescriptor => ({
  name,
  type: ConfigFieldType.Text,
  label,
  required: false,
  defaultValue: null,
  description: null,
  options: null,
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

test("an earlier provider's adapter schema arriving late does not replace the current one", async () => {
  let resolveDefaultSchema!: (schema: ConfigFieldDescriptor[]) => void;
  agentAdapterService.getConfigSchema.mockImplementation((type: string) =>
    type === "pi"
      ? new Promise((resolve) => (resolveDefaultSchema = resolve))
      : Promise.resolve([field("effort", "Effort")]),
  );
  aiProviderService.getAll.mockResolvedValue([
    provider("Alpha", "pi", true, []),
    provider("Beta", "claude-code", false, ["QA"]),
  ]);
  loopTemplateService.getAll.mockResolvedValue([
    {
      id: "tpl-1",
      name: "Dev Loop",
      description: "",
      version: 1,
      recoveryPolicy: RecoveryPolicy.AutoResume,
      nodes: [
        { id: "n-start", type: NodeType.Start, label: "Initialize", config: {} },
        { id: "n-ai", type: NodeType.AI, label: "Reviewer", config: { prompt: "p" } },
        { id: "n-cleanup", type: NodeType.Cleanup, label: "Tidy Up", config: {} },
      ],
      edges: [
        { id: "e-1", sourceNodeId: "n-start", targetNodeId: "n-ai", edgeType: EdgeType.OnSuccess },
        {
          id: "e-2",
          sourceNodeId: "n-ai",
          targetNodeId: "n-cleanup",
          edgeType: EdgeType.OnSuccess,
        },
      ],
      createdAt: "2025-01-01T00:00:00Z",
      updatedAt: "2025-01-01T00:00:00Z",
      isArchived: false,
    },
  ]);

  render(
    <MemoryRouter initialEntries={["/loop-editor/tpl-1"]}>
      <AuthContext.Provider
        value={{
          user: { id: "1", username: "test", createdAt: "" },
          token: "test-token",
          isAuthenticated: true,
          isLoading: false,
          login: vi.fn(),
          logout: vi.fn(),
        }}
      >
        <Routes>
          <Route path="/loop-editor/:templateId" element={<LoopEditor />} />
        </Routes>
      </AuthContext.Provider>
    </MemoryRouter>,
  );
  await waitFor(() => expect(screen.getByText("Reviewer")).toBeTruthy());
  await waitFor(() => expect(aiProviderService.getAll).toHaveBeenCalled());
  await act(async () => {});
  fireEvent.click(screen.getByText("Reviewer"));
  const dialog = await screen.findByRole("dialog", { name: "Node Settings" });

  // Opening on the default (Alpha) starts its schema load; the tag moves the
  // node to Beta before that load returns.
  fireEvent.change(within(dialog).getByLabelText("Provider tag"), { target: { value: "QA" } });
  expect(await within(dialog).findByLabelText("Effort")).toBeTruthy();

  await act(async () => resolveDefaultSchema([field("reasoning", "Reasoning")]));

  expect(within(dialog).getByLabelText("Effort")).toBeTruthy();
  expect(within(dialog).queryByLabelText("Reasoning")).toBeNull();
});
