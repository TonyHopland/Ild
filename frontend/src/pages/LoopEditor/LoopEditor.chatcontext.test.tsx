import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, cleanup, act } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { AuthContext } from "../../hooks/useAuth";
import { NodeType, EdgeType, RecoveryPolicy } from "../../types";
import { getOpenLoopDocument } from "../../utils/openLoopDocument";
import { setCurrentChatSessionId } from "../../services/chatSessionStore";

// Capture the hub handlers the editor registers so a test can fire a server push,
// and stub the services the editor calls on mount (mirrors ChatBubble.test.tsx).
// The chat session id comes from the real chatSessionStore (driven below), not a
// service call, so it is exercised end to end.
const { handlers, loopTemplateService, aiProviderService, agentAdapterService } = vi.hoisted(
  () => ({
    handlers: {} as Record<string, (msg: { payload: unknown }) => void>,
    loopTemplateService: { getAll: vi.fn() },
    aiProviderService: { getAll: vi.fn() },
    agentAdapterService: { getConfigSchema: vi.fn() },
  }),
);

vi.mock("../../hooks/useSignalR", () => ({
  useSignalR: () => ({
    connectionState: "connected",
    on: (event: string, handler: (msg: { payload: unknown }) => void) => {
      handlers[event] = handler;
    },
    off: (event: string) => {
      delete handlers[event];
    },
    invoke: vi.fn(() => Promise.resolve()),
  }),
}));

vi.mock("../../services/auth", () => ({
  loopTemplateService,
  aiProviderService,
  agentAdapterService,
}));

import LoopEditor from "./index";

const sampleTemplate = {
  id: "tpl-1",
  name: "Dev Loop",
  description: "Standard development loop",
  version: 3,
  recoveryPolicy: RecoveryPolicy.AutoResume,
  nodes: [
    { id: "n-start", type: NodeType.Start, label: "Initialize", config: {}, maxTraversals: null },
    { id: "n-cleanup", type: NodeType.Cleanup, label: "Tidy Up", config: {}, maxTraversals: null },
  ],
  edges: [
    {
      id: "e-1",
      sourceNodeId: "n-start",
      targetNodeId: "n-cleanup",
      edgeType: EdgeType.OnSuccess,
      maxTraversals: null,
    },
  ],
  createdAt: "2025-01-01T00:00:00Z",
  updatedAt: "2025-01-01T00:00:00Z",
  isArchived: false,
};

// A complete ild-loop-template/v1 document the AI "pushes" — different node labels
// so a successful apply is visible on the canvas.
const aiDocument = {
  $schema: "ild-loop-template/v1",
  name: "AI Reworked Loop",
  description: "rebuilt by the agent",
  recoveryPolicy: RecoveryPolicy.AutoResume,
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
};

const authValue = {
  user: { id: "1", username: "test", createdAt: "" },
  token: "test-token",
  isAuthenticated: true,
  isLoading: false,
  login: vi.fn(),
  logout: vi.fn(),
};

function renderEditorWithOpenTemplate() {
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
}

function pushLoopUpdate(chatSessionId: string, document: object) {
  act(() => {
    handlers.ChatLoopUpdate?.({
      payload: { chatSessionId, document: JSON.stringify(document) },
    });
  });
}

afterEach(() => {
  cleanup();
  for (const k of Object.keys(handlers)) delete handlers[k];
  vi.clearAllMocks();
  // The store is module-level global; reset so a session id can't leak between tests.
  setCurrentChatSessionId(null);
});

describe("Loop Editor — loop editor context (ADR-0011)", () => {
  test("exposes the open loop as a live ild-loop-template/v1 document for the chat", async () => {
    loopTemplateService.getAll.mockResolvedValue([sampleTemplate]);
    aiProviderService.getAll.mockResolvedValue([]);

    renderEditorWithOpenTemplate();
    await waitFor(() => expect(screen.getByText("Initialize")).toBeTruthy());

    const live = getOpenLoopDocument();
    expect(live).toBeTruthy();
    expect(live!.$schema).toBe("ild-loop-template/v1");
    expect(live!.name).toBe("Dev Loop");
    expect(live!.nodes.map((n) => n.label).sort()).toEqual(["Initialize", "Tidy Up"]);
  });

  test("applies a pushed loop document live to the canvas for the matching session", async () => {
    loopTemplateService.getAll.mockResolvedValue([sampleTemplate]);
    aiProviderService.getAll.mockResolvedValue([]);
    // A session exists before the editor mounts (seeded from the shared store).
    setCurrentChatSessionId("s1");

    renderEditorWithOpenTemplate();
    await waitFor(() => expect(screen.getByText("Initialize")).toBeTruthy());

    pushLoopUpdate("s1", aiDocument);

    // New labels replace the old ones — the canvas updated in place.
    await waitFor(() => expect(screen.getByText("Boot Up")).toBeTruthy());
    expect(screen.getByText("Wind Down")).toBeTruthy();
    expect(screen.queryByText("Initialize")).toBeNull();
    expect(screen.queryByText("Tidy Up")).toBeNull();
  });

  test("applies edits for a session created after the editor mounted", async () => {
    loopTemplateService.getAll.mockResolvedValue([sampleTemplate]);
    aiProviderService.getAll.mockResolvedValue([]);
    // No session yet when the editor mounts — the user opens the chat and starts
    // one only afterwards. The one-shot-resolve bug would leave this stuck on null.
    setCurrentChatSessionId(null);

    renderEditorWithOpenTemplate();
    await waitFor(() => expect(screen.getByText("Initialize")).toBeTruthy());

    // A push now would be ignored — there is no session to match yet.
    pushLoopUpdate("s-late", aiDocument);
    expect(screen.getByText("Initialize")).toBeTruthy();
    expect(screen.queryByText("Boot Up")).toBeNull();

    // ChatBubble starts a session and publishes it; the editor must pick it up.
    act(() => setCurrentChatSessionId("s-late"));
    pushLoopUpdate("s-late", aiDocument);

    await waitFor(() => expect(screen.getByText("Boot Up")).toBeTruthy());
    expect(screen.queryByText("Initialize")).toBeNull();
  });

  test("ignores a push addressed to a different chat session", async () => {
    loopTemplateService.getAll.mockResolvedValue([sampleTemplate]);
    aiProviderService.getAll.mockResolvedValue([]);
    setCurrentChatSessionId("s1");

    renderEditorWithOpenTemplate();
    await waitFor(() => expect(screen.getByText("Initialize")).toBeTruthy());

    pushLoopUpdate("someone-else", aiDocument);

    // The canvas is untouched — the event was for another user's session.
    expect(screen.getByText("Initialize")).toBeTruthy();
    expect(screen.queryByText("Boot Up")).toBeNull();
  });

  test("rejects a malformed document with a banner and leaves the loop untouched", async () => {
    loopTemplateService.getAll.mockResolvedValue([sampleTemplate]);
    aiProviderService.getAll.mockResolvedValue([]);
    setCurrentChatSessionId("s1");

    renderEditorWithOpenTemplate();
    await waitFor(() => expect(screen.getByText("Initialize")).toBeTruthy());

    // Wrong $schema — parseImportFile must reject it.
    pushLoopUpdate("s1", { ...aiDocument, $schema: "not-a-loop/v9" });

    await waitFor(() => expect(screen.getByText(/AI loop edit rejected/i)).toBeTruthy());
    // The original graph survives the rejected edit.
    expect(screen.getByText("Initialize")).toBeTruthy();
    expect(screen.queryByText("Boot Up")).toBeNull();
  });
});
